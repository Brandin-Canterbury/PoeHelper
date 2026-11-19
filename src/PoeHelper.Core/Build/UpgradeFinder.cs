using System.Globalization;
using System.Text.Json.Nodes;
using PoeHelper.Core.Data;
using PoeHelper.Core.Pricing;
using PoeHelper.Core.Stats;
using PoeHelper.Core.Trade;

namespace PoeHelper.Core.Build;

public enum UpgradePriority { Auto, Life, Damage }

public sealed record StatWeight(string TradeId, string Label, double Weight);

public sealed class UpgradePlan
{
    public required string Slot { get; init; }
    public required JsonObject Query { get; init; }
    /// <summary>Plain-language list of what the search insists on.</summary>
    public List<string> Constraints { get; } = [];
    public List<StatWeight> Weights { get; } = [];
    public double CurrentWeightedValue { get; init; }
    public UpgradePriority Priority { get; init; }
    public string? SortStat { get; init; }
    /// <summary>True when the search used a weighted stat group (needs a logged-in session).</summary>
    public bool Weighted { get; init; }
}

public sealed record UpgradeCandidate(Listing Listing, SlotComparison Compare, string? Whisper);

public sealed class UpgradeResult
{
    public required UpgradePlan Plan { get; init; }
    public required string SearchUrl { get; init; }
    public int Total { get; init; }
    public List<UpgradeCandidate> Candidates { get; } = [];
    public int Rejected { get; set; }
    public string? Note { get; init; }
}

/// <summary>
/// "Find me something better for this slot, Instant Buyout, under my budget":
/// 1. what the slot must cover: resists that fall under cap and attributes that fall short without it;
/// 2. what is worth paying for: each stat's value in life%+damage% for this build, as trade weights;
/// 3. the search, sorted by weighted value; then every listing is re-checked with the full compare model.
/// </summary>
public static class UpgradeFinder
{
    /// <summary>Candidate value stats: trade text/id, probe line, probe size, pseudo?, a typical good roll.</summary>
    static readonly (string Text, string Probe, double Unit, bool Pseudo, double TypicalRoll)[] ValueStats =
    [
        ("pseudo.pseudo_total_life", "+10 to maximum Life", 10, true, 80),
        ("#% increased maximum Life", "1% increased maximum Life", 1, false, 8),
        ("+#% to Fire Damage over Time Multiplier", "+1% to Fire Damage over Time Multiplier", 1, false, 25),
        ("+#% to Damage over Time Multiplier", "+1% to Damage over Time Multiplier", 1, false, 20),
        ("#% increased Fire Damage", "1% increased Fire Damage", 1, false, 35),
        ("#% increased Burning Damage", "1% increased Burning Damage", 1, false, 30),
        ("#% increased Spell Damage", "1% increased Spell Damage", 1, false, 60),
        ("#% increased Elemental Damage", "1% increased Elemental Damage", 1, false, 35),
        ("#% increased Damage over Time", "1% increased Damage over Time", 1, false, 30),
        ("#% increased Area Damage", "1% increased Area Damage", 1, false, 20),
        ("pseudo.pseudo_total_strength", "+10 to Strength", 10, true, 45),
    ];



    public static string? CategoryFor(string slot, CharacterBuild build) => slot switch
    {
        "Helm" => "armour.helmet",
        "BodyArmour" => "armour.chest",
        "Gloves" => "armour.gloves",
        "Boots" => "armour.boots",
        "Belt" => "accessory.belt",
        "Amulet" => "accessory.amulet",
        "Ring" or "Ring2" => "accessory.ring",
        "Offhand" => "armour.shield",
        "Weapon" => build.Items.ContainsKey("Offhand") ? "weapon.one" : "weapon",
        _ => null,
    };

    /// <summary>Most weighted stats sent when logged in (weighted groups need a session; more = more complex).</summary>
    public const int MaxWeightedStats = 6;

    public static UpgradePlan Plan(CharacterBuild build, string slot, TradeData data, double budget, string currency,
        UpgradePriority priority = UpgradePriority.Auto, bool weighted = false)
    {
        build.Items.TryGetValue(slot, out var current);
        var now = BuildCalculator.Compute(build);
        var without = BuildCalculator.Compute(build.WithItem(slot, null));
        var constraints = new List<string>();
        var andFilters = new JsonArray();

        void Require(string id, double min, string text)
        {
            andFilters.Add(new JsonObject { ["id"] = id, ["value"] = new JsonObject { ["min"] = min }, ["disabled"] = false });
            constraints.Add(text);
        }

        // 1. Resist floors. A fire mod also counts toward cold/lightning when the build converts fire res.
        double conv = now.Totals.FireResToColdLightningPct / 100;
        double fireNeed = Math.Max(0, Math.Ceiling(now.Fire.Max - without.Fire.Total));
        double coldNeed = Math.Max(0, Math.Ceiling(now.Cold.Max - without.Cold.Total - fireNeed * conv));
        double lightNeed = Math.Max(0, Math.Ceiling(now.Lightning.Max - without.Lightning.Total - fireNeed * conv));
        if (fireNeed > 0) Require("pseudo.pseudo_total_fire_resistance", fireNeed, $"≥ {fireNeed}% fire res (keeps fire capped)");
        if (coldNeed > 0) Require("pseudo.pseudo_total_cold_resistance", coldNeed, $"≥ {coldNeed}% cold res (keeps cold capped)");
        if (lightNeed > 0) Require("pseudo.pseudo_total_lightning_resistance", lightNeed, $"≥ {lightNeed}% lightning res (keeps lightning capped)");

        // 2. Attribute floors: the highest gem/gear requirement must still be met (the new item's own
        //    requirement is limited separately below).
        var gems = build.Items.Values.SelectMany(i => i.Gems).ToList();
        var others = build.Items.Values.Where(i => i.Slot != slot).ToList();
        int NeedOf(Func<GemInfo, int> g, Func<EquippedItem, int> i) => Math.Max(gems.Select(g).DefaultIfEmpty(0).Max(), others.Select(i).DefaultIfEmpty(0).Max());
        foreach (var (attr, need, have) in new[]
                 {
                     ("strength", NeedOf(g => g.Str, i => i.ReqStr), without.Str),
                     ("dexterity", NeedOf(g => g.Dex, i => i.ReqDex), without.Dex),
                     ("intelligence", NeedOf(g => g.Int, i => i.ReqInt), without.Int),
                 })
        {
            double gap = Math.Ceiling(need - have);
            if (gap > 0) Require($"pseudo.pseudo_total_{attr}", gap, $"≥ {gap} {attr} (gem requirements: need {need})");
        }

        // 3. What's worth paying for, in "life-equivalents" (1 weight = the value of 1 maximum life).
        var plan = new UpgradePlan { Slot = slot, Query = new JsonObject() }; // collects weights; rebuilt below with the query
        double baseScore = Score(now, now);
        double lifeValue = 0;
        var weights = new List<(string Id, string Label, double PerUnit, double Typical)>();
        foreach (var (text, probe, unit, pseudo, typical) in ValueStats)
        {
            var probed = build.WithItem("__probe", new EquippedItem { Slot = "__probe", Name = "probe", Mods = [probe] });
            double gain = (Score(now, BuildCalculator.Compute(probed)) - baseScore) / unit;
            if (gain <= 0) continue;
            string? id = pseudo ? text : data.StatsBySkeleton("explicit", text).FirstOrDefault()?.Id;
            if (id is null) continue;
            if (text == "pseudo.pseudo_total_life") lifeValue = gain;
            weights.Add((id, pseudo ? probe.Replace("+10", "+#") : text, gain, typical));
        }
        if (lifeValue <= 0) lifeValue = weights.Count > 0 ? weights.Max(w => w.PerUnit) : 1;

        // Logged in: one weighted group of the most valuable stats, sorted by weighted value.
        // Anonymous: GGG rejects weighted groups as "too complex", so the search sorts by the single
        // most valuable stat (value per point × a typical roll). Either way the full model ranks the results.
        foreach (var (id, label, perUnit, _) in weights.OrderByDescending(w => w.PerUnit * w.Typical))
        {
            double w = Math.Round(perUnit / lifeValue, 2);
            if (w >= 0.01) plan.Weights.Add(new StatWeight(id, label, w));
        }
        // Which stat the search sorts on: life for armour and jewellery, the best damage stat for weapons.
        if (priority == UpgradePriority.Auto) priority = slot is "Weapon" ? UpgradePriority.Damage : UpgradePriority.Life;
        var top = priority == UpgradePriority.Life
            ? plan.Weights.FirstOrDefault(w => w.TradeId == "pseudo.pseudo_total_life") ?? plan.Weights.FirstOrDefault()
            : plan.Weights.FirstOrDefault(w => !w.TradeId.StartsWith("pseudo.") && !w.Label.Contains("Life")) ?? plan.Weights.FirstOrDefault();
        var result0Priority = priority;
        double currentValue = current is null ? 0 : WeightedValue(current.Mods, plan.Weights);
        // Life always goes in (it's the main stat on most slots); damage stats fill the remaining places.
        var weightGroup = !weighted ? [] : plan.Weights.Where(w => w.TradeId == "pseudo.pseudo_total_life" || w.Label.Contains("maximum Life"))
            .Concat(plan.Weights.Where(w => w.TradeId != "pseudo.pseudo_total_life" && !w.Label.Contains("maximum Life")))
            .Take(MaxWeightedStats).ToList();
        double currentWeighted = current is null ? 0 : WeightedValue(current.Mods, weightGroup);
        if (weightGroup.Count > 0)
            constraints.Add($"weighted value ≥ {currentWeighted:0} (your current item's): {string.Join(", ", weightGroup.Select(w => $"{w.Label.Replace("#", "").Replace("+", "").Trim()} ×{w.Weight}"))}");
        else if (top is not null)
        {
            double currentTop = current is null ? 0 : WeightedValue(current.Mods, [top with { Weight = 1 }]);
            if (currentTop > 0)
                Require(top.TradeId, Math.Floor(currentTop), $"≥ {currentTop:0} {top.Label.Replace("#", "").Replace("+", "").Trim()} (at least your current item's)");
        }

        var filters = new JsonObject
        {
            ["trade_filters"] = new JsonObject
            {
                ["filters"] = new JsonObject
                {
                    ["price"] = new JsonObject { ["max"] = budget, ["option"] = currency },
                },
            },
        };
        if (CategoryFor(slot, build) is { } cat)
            filters["type_filters"] = new JsonObject { ["filters"] = new JsonObject { ["category"] = new JsonObject { ["option"] = cat } } };

        if (current is { Gems.Count: > 0 })
        {
            var sockets = new JsonObject { ["sockets"] = new JsonObject { ["min"] = current.Gems.Count } };
            if (current.MaxLinks >= 2) sockets["links"] = new JsonObject { ["min"] = current.MaxLinks };
            filters["socket_filters"] = new JsonObject { ["filters"] = sockets };
            constraints.Add($"≥ {current.Gems.Count} sockets" + (current.MaxLinks >= 2 ? $", {current.MaxLinks}-link (for your gems)" : ""));
        }
        constraints.Add($"wearable now: level ≤ {build.Level}, attribute requirements met (checked on each result)");
        constraints.Add($"Instant Buyout, ≤ {budget.ToString("0.##", CultureInfo.InvariantCulture)} {(currency == "divine" ? "div" : "c")}");

        // The weight group goes first: GGG's sort key is "statgroup.<index>", and index 0 is the one the
        // trade site itself uses (the user hit "Unknown sort key: statgroup.1" with it second).
        var stats = new JsonArray();
        if (weightGroup.Count > 0)
            stats.Add(new JsonObject
            {
                ["type"] = "weight",
                ["value"] = new JsonObject { ["min"] = Math.Floor(currentWeighted) },
                ["filters"] = new JsonArray(weightGroup.Select(w => (JsonNode)new JsonObject
                {
                    ["id"] = w.TradeId, ["value"] = new JsonObject { ["weight"] = w.Weight }, ["disabled"] = false,
                }).ToArray()),
            });
        stats.Add(new JsonObject { ["type"] = "and", ["filters"] = andFilters });

        var query = new JsonObject
        {
            ["query"] = new JsonObject
            {
                ["status"] = new JsonObject { ["option"] = "securable" },
                ["stats"] = stats,
                ["filters"] = filters,
            },
            ["sort"] = weightGroup.Count > 0 ? new JsonObject { ["statgroup.0"] = "desc" }
                     : top is not null ? new JsonObject { [$"stat.{top.TradeId}"] = "desc" }
                     : new JsonObject { ["price"] = "asc" },
        };
        var result = new UpgradePlan
        {
            Slot = slot, Query = query, CurrentWeightedValue = currentValue, Priority = result0Priority,
            SortStat = weightGroup.Count > 0 ? "weighted value" : top?.Label, Weighted = weightGroup.Count > 0,
        };
        result.Constraints.AddRange(constraints);
        result.Weights.AddRange(plan.Weights);
        return result;
    }

    public static async Task<UpgradeResult> RunAsync(TradeClient client, TradeData data, CurrencyRates rates, CharacterBuild build,
        string slot, double budget, string currency, string league, UpgradePriority priority = UpgradePriority.Auto,
        int fetch = 20, CancellationToken ct = default)
    {
        await rates.EnsureFreshAsync(client, league, ct);
        var plan = Plan(build, slot, data, budget, currency, priority, weighted: client.HasSession);
        SearchResult search;
        string? note = null;
        try
        {
            search = await client.SearchAsync(league, plan.Query, ct);
        }
        catch (TradeApiException ex) when (plan.Weighted && ex.Message.Contains("too complex", StringComparison.OrdinalIgnoreCase))
        {
            // The session didn't raise the limit (expired cookie?): fall back to the single-stat search.
            plan = Plan(build, slot, data, budget, currency, priority, weighted: false);
            search = await client.SearchAsync(league, plan.Query, ct);
            note = "Weighted search was refused (session expired?), so this used the single-stat search.";
        }
        catch (TradeApiException ex) when (plan.Weighted && ex.Message.Contains("sort key", StringComparison.OrdinalIgnoreCase))
        {
            // Keep the weighted filter but let GGG sort by price; results are ranked locally anyway.
            plan.Query["sort"] = new JsonObject { ["price"] = "asc" };
            search = await client.SearchAsync(league, plan.Query, ct);
            note = "The trade site wouldn't sort by weighted value, so these are the cheapest items above your current one's value.";
        }
        var result = new UpgradeResult
        {
            Plan = plan, Total = search.Total, Note = note,
            SearchUrl = $"{TradeClient.BaseUrl}/trade/search/{Uri.EscapeDataString(league)}/{search.Id}",
        };
        if (search.Ids.Count == 0) return result;

        var before = BuildCalculator.Compute(build);
        foreach (var r in await client.FetchAsync(search, fetch, ct))
        {
            if (r["item"] is not JsonObject item) continue;
            var listing = PriceAnalyzer.ParseListing(r, rates);
            var candidate = CharacterBuild.FromApiItem(slot, item);
            int reqLevel = item["requirements"]?.AsArray().Where(q => q?["name"]?.GetValue<string>() == "Level")
                .Select(q => Items.ItemParser.FirstInt(q?["values"]?[0]?[0]?.GetValue<string>() ?? "0")).FirstOrDefault() ?? 0;
            if (reqLevel > build.Level) { result.Rejected++; continue; }
            bool twoHanded = item["properties"]?.AsArray().Any(p => (p?["name"]?.GetValue<string>() ?? "").StartsWith("Two Hand")) == true
                             || (item["baseType"]?.GetValue<string>() ?? "") is var b && (b.Contains("Staff") || b.EndsWith("Bow"));
            var cmp = GearCompare.CompareEquipped(build, slot, candidate, twoHanded, before);
            if (cmp.Overall == Sentiment.Bad) { result.Rejected++; continue; }
            result.Candidates.Add(new UpgradeCandidate(listing, cmp, r["listing"]?["whisper"]?.GetValue<string>()));
        }
        // Best first; ties go to the cheaper listing.
        result.Candidates.Sort((x, y) =>
        {
            int c = y.Compare.Score.CompareTo(x.Compare.Score);
            return c != 0 ? c : (x.Listing.Chaos ?? double.MaxValue).CompareTo(y.Listing.Chaos ?? double.MaxValue);
        });
        return result;
    }

    /// <summary>The score GearCompare ranks by: life % change + damage % change.</summary>
    static double Score(BuildStats b, BuildStats a) =>
        (b.Life > 0 ? (a.Life / b.Life - 1) * 100 : 0) + (b.RfDamageIndex > 0 ? (a.RfDamageIndex / b.RfDamageIndex - 1) * 100 : 0);

    static double WeightedValue(IEnumerable<string> mods, IEnumerable<StatWeight> weights)
    {
        var t = StatTotals.FromTexts(mods);
        double total = 0;
        foreach (var w in weights)
        {
            double v = w.TradeId switch
            {
                "pseudo.pseudo_total_life" => t.Life,
                "pseudo.pseudo_total_strength" => t.Str,
                _ => w.Label switch
                {
                    "#% increased maximum Life" => t.LifePct,
                    "+#% to Fire Damage over Time Multiplier" => t.FireDotMulti,
                    "+#% to Damage over Time Multiplier" => t.DotMulti,
                    "#% increased Fire Damage" => t.FireDamagePct,
                    "#% increased Burning Damage" => t.BurningDamagePct,
                    "#% increased Spell Damage" => t.SpellDamagePct,
                    "#% increased Elemental Damage" => t.ElementalDamagePct,
                    "#% increased Damage over Time" => t.DamageOverTimePct,
                    "#% increased Area Damage" => t.AreaDamagePct,
                    _ => 0,
                },
            };
            total += v * w.Weight;
        }
        return total;
    }
}
