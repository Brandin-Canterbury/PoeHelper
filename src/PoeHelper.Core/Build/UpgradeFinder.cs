using System.Globalization;
using System.Text.Json.Nodes;
using PoeHelper.Core.Data;
using PoeHelper.Core.Pricing;
using PoeHelper.Core.Stats;
using PoeHelper.Core.Trade;

namespace PoeHelper.Core.Build;

public sealed record StatWeight(string TradeId, string Label, double Weight);

/// <summary>What Find upgrade looks for: the tracked stats from Settings, and whether chaos res counts as a resist.</summary>
public sealed record UpgradeOptions(IReadOnlyList<TrackedStat> Tracked, bool IncludeChaos = false)
{
    public static UpgradeOptions Default { get; } = new(TrackedStats.Resolve(null));
}

public sealed class UpgradePlan
{
    public required string Slot { get; init; }
    public required JsonObject Query { get; init; }
    /// <summary>Plain-language list of what the search insists on.</summary>
    public List<string> Constraints { get; } = [];
    public List<StatWeight> Weights { get; } = [];
    public required UpgradeOptions Options { get; init; }
    public string? SortStat { get; init; }
    /// <summary>True when the search used a weighted stat group (needs a logged-in session).</summary>
    public bool Weighted { get; init; }
}

/// <param name="ResistShortfall">How far the resists end up under their caps, summed (0 = all capped). Lower ranks first.</param>
/// <param name="TrackedScore">The tracked stats' changes, each in "good rolls". Higher ranks first.</param>
/// <param name="Rows">The compare rows to show: capped resists are left out.</param>
public sealed record UpgradeCandidate(Listing Listing, SlotComparison Compare, string? Whisper, string? Url,
    double ResistShortfall, double TrackedScore, IReadOnlyList<CompareRow> Rows, IReadOnlyList<string> Warnings, string Gains);

public sealed class UpgradeResult
{
    public required UpgradePlan Plan { get; init; }
    public required string SearchUrl { get; init; }
    public int Total { get; init; }
    public List<UpgradeCandidate> Candidates { get; } = [];
    public int Rejected { get; set; }
    public string? Note { get; init; }
    /// <summary>The skill "damage" was scored for.</summary>
    public SkillProfile? Skill { get; init; }
}

/// <summary>
/// "Find me something better for this slot, Instant Buyout, under my budget":
/// 1. what the slot must cover: resists that fall under cap and attributes that fall short without it;
/// 2. what to look for: the tracked stats from Settings (damage = the selected skill's damage), as trade weights;
/// 3. every listing is re-checked with the full compare model and ranked: resists first, then the tracked stats.
/// </summary>
public static class UpgradeFinder
{
    /// <summary>Stats that feed skill damage: trade text, probe line, probe size, a typical good roll.</summary>
    static readonly (string Text, string Probe, double Unit, double TypicalRoll)[] DamageStats =
    [
        ("+#% to Fire Damage over Time Multiplier", "+1% to Fire Damage over Time Multiplier", 1, 25),
        ("+#% to Cold Damage over Time Multiplier", "+1% to Cold Damage over Time Multiplier", 1, 25),
        ("+#% to Chaos Damage over Time Multiplier", "+1% to Chaos Damage over Time Multiplier", 1, 25),
        ("+#% to Physical Damage over Time Multiplier", "+1% to Physical Damage over Time Multiplier", 1, 25),
        ("+#% to Damage over Time Multiplier", "+1% to Damage over Time Multiplier", 1, 20),
        ("#% increased Fire Damage", "1% increased Fire Damage", 1, 35),
        ("#% increased Cold Damage", "1% increased Cold Damage", 1, 35),
        ("#% increased Lightning Damage", "1% increased Lightning Damage", 1, 35),
        ("#% increased Global Physical Damage", "1% increased Global Physical Damage", 1, 30),
        ("#% increased Chaos Damage", "1% increased Chaos Damage", 1, 30),
        ("#% increased Burning Damage", "1% increased Burning Damage", 1, 30),
        ("#% increased Spell Damage", "1% increased Spell Damage", 1, 60),
        ("#% increased Elemental Damage", "1% increased Elemental Damage", 1, 35),
        ("#% increased Damage over Time", "1% increased Damage over Time", 1, 30),
        ("#% increased Area Damage", "1% increased Area Damage", 1, 20),
        ("#% increased Attack Damage", "1% increased Attack Damage", 1, 25),
        ("#% increased Projectile Damage", "1% increased Projectile Damage", 1, 20),
        ("#% increased Elemental Damage with Attack Skills", "1% increased Elemental Damage with Attack Skills", 1, 35),
        ("Minions deal #% increased Damage", "Minions deal 1% increased Damage", 1, 30),
        ("#% increased Attack Speed", "1% increased Attack Speed", 1, 12),
        ("#% increased Cast Speed", "1% increased Cast Speed", 1, 15),
        ("Minions have #% increased Attack and Cast Speed", "Minions have 1% increased Attack and Cast Speed", 1, 10),
        ("#% increased Global Critical Strike Chance", "1% increased Global Critical Strike Chance", 1, 30),
        ("+#% to Global Critical Strike Multiplier", "+1% to Global Critical Strike Multiplier", 1, 30),
        ("Adds # to # Physical Damage to Attacks", "Adds 5 to 15 Physical Damage to Attacks", 10, 12),
        ("Adds # to # Fire Damage to Attacks", "Adds 5 to 15 Fire Damage to Attacks", 10, 25),
        ("Adds # to # Cold Damage to Attacks", "Adds 5 to 15 Cold Damage to Attacks", 10, 22),
        ("Adds # to # Lightning Damage to Attacks", "Adds 5 to 15 Lightning Damage to Attacks", 10, 35),
        ("Adds # to # Fire Damage to Spells", "Adds 5 to 15 Fire Damage to Spells", 10, 30),
        ("Adds # to # Cold Damage to Spells", "Adds 5 to 15 Cold Damage to Spells", 10, 28),
        ("Adds # to # Lightning Damage to Spells", "Adds 5 to 15 Lightning Damage to Spells", 10, 40),
        ("+# to Level of all Minion Skill Gems", "+1 to Level of all Minion Skill Gems", 1, 1),
        ("+# to Level of all Fire Spell Skill Gems", "+1 to Level of all Fire Spell Skill Gems", 1, 1),
        ("+# to Level of all Cold Spell Skill Gems", "+1 to Level of all Cold Spell Skill Gems", 1, 1),
        ("+# to Level of all Lightning Spell Skill Gems", "+1 to Level of all Lightning Spell Skill Gems", 1, 1),
        ("+# to Level of all Chaos Spell Skill Gems", "+1 to Level of all Chaos Spell Skill Gems", 1, 1),
        ("+# to Level of all Physical Spell Skill Gems", "+1 to Level of all Physical Spell Skill Gems", 1, 1),
    ];

    const string ChaosResId = "pseudo.pseudo_total_chaos_resistance";
    /// <summary>When chaos res counts and isn't capped, each point is worth this many "good rolls" in the weighted search (≈ 3 life).</summary>
    const double ChaosPerPoint = 3 / 70.0;
    /// <summary>How much of the tracked stats (in good rolls, e.g. ~70 life) an item may cost if it fixes resists.</summary>
    public const double ResistFixAllowance = 1;

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
        UpgradeOptions? options = null, bool weighted = false)
    {
        options ??= UpgradeOptions.Default;
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
        // Chaos, when ticked: keep it capped if it is; otherwise more chaos res is the first thing the search wants.
        bool chaosWanted = options.IncludeChaos && !now.Chaos.Capped;
        if (options.IncludeChaos && now.Chaos.Capped)
        {
            double chaosNeed = Math.Max(0, Math.Ceiling(now.Chaos.Max - without.Chaos.Total));
            if (chaosNeed > 0) Require(ChaosResId, chaosNeed, $"≥ {chaosNeed}% chaos res (keeps chaos capped)");
        }

        // 2. Attribute floors: the highest gem/gear requirement must still be met (the new item's own
        //    requirement is checked on each result).
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

        // 3. The tracked stats as trade weights, in "good rolls" per point. Damage = what each damage stat adds
        //    to the selected skill, probed through the model, so only stats that scale that skill get a weight.
        var weights = new Dictionary<string, (string Label, double PerUnit, double Typical)>();
        void Weigh(string id, string label, double perUnit, double typical)
        {
            if (perUnit <= 0) return;
            weights[id] = weights.TryGetValue(id, out var w) ? (w.Label, w.PerUnit + perUnit, Math.Max(w.Typical, typical)) : (label, perUnit, typical);
        }
        foreach (var ts in options.Tracked)
        {
            if (ts.IsDamage)
            {
                foreach (var (text, probe, unit, typical) in DamageStats)
                {
                    var probed = build.WithItem("__probe", new EquippedItem { Slot = "__probe", Name = "probe", Mods = [probe] });
                    double gain = GearCompare.DamageChangePct(now, BuildCalculator.Compute(probed)) / unit / ts.Typical;
                    if (data.StatsBySkeleton("explicit", text).FirstOrDefault()?.Id is { } id) Weigh(id, text, gain, typical);
                }
                continue;
            }
            string? tid = ts.TradeId ?? (ts.TradeText is { } tt ? data.StatsBySkeleton("explicit", tt).FirstOrDefault()?.Id : null);
            if (tid is not null) Weigh(tid, ts.TradeText ?? ts.Label, 1 / ts.Typical, ts.Typical);
        }
        var ranked = weights.OrderByDescending(w => w.Value.PerUnit * w.Value.Typical)
            .Select(w => new StatWeight(w.Key, w.Value.Label, Math.Round(w.Value.PerUnit * 100, 2)))
            .Where(w => w.Weight >= 0.01).ToList();
        if (chaosWanted) ranked.Insert(0, new StatWeight(ChaosResId, "Chaos resistance", Math.Round(ChaosPerPoint * 100, 2)));

        // Logged in: one weighted group (chaos first when it counts, then the tracked stats), sorted by weighted value.
        // Anonymous: GGG rejects weighted groups as "too complex", so the search sorts by the top stat alone.
        // Either way, every result is re-ranked by the full model.
        var weightGroup = weighted ? ranked.Take(MaxWeightedStats).ToList() : [];
        var top = ranked.FirstOrDefault();

        double currentWeighted = current is null ? 0 : WeightedValue(current.Mods, weightGroup);
        if (weightGroup.Count > 0)
            constraints.Add($"weighted value ≥ {currentWeighted:0} (your current item's): {string.Join(", ", weightGroup.Select(w => $"{Plain(w.Label)} ×{w.Weight}"))}");
        else if (current is not null)
        {
            // Anonymous: the search sorts on one stat, so also keep the tracked stat the current item gives most
            // (e.g. its life) at least as good, or the fetched listings would mostly give it up.
            var keep = ranked.Where(w => w.TradeId != ChaosResId)
                .Select(w => (w, value: WeightedValue(current.Mods, [w with { Weight = 1 }])))
                .OrderByDescending(x => x.value * x.w.Weight).FirstOrDefault();
            if (keep.w is not null && keep.value > 0)
                Require(keep.w.TradeId, Math.Floor(keep.value), $"≥ {keep.value:0} {Plain(keep.w.Label)} (at least your current item's)");
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
            Slot = slot, Query = query, Options = options,
            SortStat = weightGroup.Count > 0 ? "weighted value" : top is null ? "price" : Plain(top.Label), Weighted = weightGroup.Count > 0,
        };
        result.Constraints.AddRange(constraints);
        result.Weights.AddRange(ranked);
        return result;
    }

    static string Plain(string label) => label.Replace("#", "").Replace("+", "").Replace("  ", " ").Trim();

    public static async Task<UpgradeResult> RunAsync(TradeClient client, TradeData data, CurrencyRates rates, CharacterBuild build,
        string slot, double budget, string currency, string league, UpgradeOptions? options = null,
        int fetch = 20, CancellationToken ct = default)
    {
        options ??= UpgradeOptions.Default;
        await rates.EnsureFreshAsync(client, league, ct);
        var plan = Plan(build, slot, data, budget, currency, options, weighted: client.HasSession);
        SearchResult search;
        string? note = null;
        try
        {
            search = await client.SearchAsync(league, plan.Query, ct);
        }
        catch (TradeApiException ex) when (plan.Weighted && ex.Message.Contains("too complex", StringComparison.OrdinalIgnoreCase))
        {
            // The session didn't raise the limit (expired cookie?): fall back to the single-stat search.
            plan = Plan(build, slot, data, budget, currency, options, weighted: false);
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
            Plan = plan, Total = search.Total, Note = note, Skill = build.Skill,
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
            // Rings: the result is for the ring being searched. Say so if the other ring would break something.
            if (slot is "Ring" or "Ring2" && build.Items.ContainsKey(slot == "Ring" ? "Ring2" : "Ring"))
            {
                var other = GearCompare.CompareEquipped(build, slot == "Ring" ? "Ring2" : "Ring", candidate, false, before);
                if (other.Overall == Sentiment.Bad)
                    cmp.Warnings.Insert(0, $"Only for your {cmp.SlotLabel}. In place of your {other.SlotLabel} ({other.Current?.Name}): " +
                                           string.Join(" ", other.Warnings.Where(w => !w.StartsWith("Only for"))));
            }
            var c = Evaluate(cmp, options, listing, r["listing"]?["whisper"]?.GetValue<string>(),
                ListingUrl(plan.Query, league, r["listing"]?["account"]?["name"]?.GetValue<string>(), item["baseType"]?.GetValue<string>()));
            if (c is null) { result.Rejected++; continue; }
            result.Candidates.Add(c);
        }
        result.Candidates.Sort(Rank);
        return result;
    }

    /// <summary>Resists first (least under cap), then the tracked stats, then the cheaper listing.</summary>
    public static int Rank(UpgradeCandidate x, UpgradeCandidate y)
    {
        int c = Math.Round(x.ResistShortfall).CompareTo(Math.Round(y.ResistShortfall));
        if (c == 0) c = y.TrackedScore.CompareTo(x.TrackedScore);
        return c != 0 ? c : (x.Listing.Chaos ?? double.MaxValue).CompareTo(y.Listing.Chaos ?? double.MaxValue);
    }

    /// <summary>Sum of how far each counted resist sits under its cap.</summary>
    public static double ResistShortfall(BuildStats s, bool includeChaos) =>
        Short(s.Fire) + Short(s.Cold) + Short(s.Lightning) + (includeChaos ? Short(s.Chaos) : 0);

    static double Short(Resist r) => Math.Max(0, r.Max - r.Total);

    /// <summary>
    /// Scores one candidate, or null when it isn't an upgrade by the user's rules: it breaks a resist cap, a
    /// requirement or the links; it leaves resists no better and the tracked stats no better; or it fixes resists
    /// but costs more than <see cref="ResistFixAllowance"/> good rolls of the tracked stats.
    /// </summary>
    public static UpgradeCandidate? Evaluate(SlotComparison cmp, UpgradeOptions options, Listing listing, string? whisper = null, string? url = null)
    {
        if (cmp.Overall == Sentiment.Bad) return null;
        var (b, a) = (cmp.Before, cmp.After);
        if (options.IncludeChaos && b.Chaos.Capped && !a.Chaos.Capped) return null;
        double shortBefore = ResistShortfall(b, options.IncludeChaos), shortAfter = ResistShortfall(a, options.IncludeChaos);
        double score = options.Tracked.Sum(t => t.Change(b, a) / t.Typical);
        bool fixesResists = shortAfter < shortBefore - 0.5;
        // Not an upgrade: nothing tracked gained and resists no better, or it costs more than a roll to fix resists.
        if (!fixesResists && score <= 0.001) return null;
        if (fixesResists && score < -ResistFixAllowance) return null;

        // Rows: resists only when they end up under cap (or chaos isn't counted at all); everything else as compared.
        var rows = new List<CompareRow>();
        var gains = new List<string>();
        foreach (var row in cmp.Rows)
        {
            if (ResistOf(row.Label, a) is { } res)
            {
                bool chaos = row.Label.StartsWith("Chaos");
                if ((chaos && !options.IncludeChaos) || res.Capped || row.Delta.Length == 0) continue;
                gains.Add($"{row.Delta}% {row.Label.Replace(" res", "").ToLowerInvariant()} res (now {res.Effective:0}%)");
            }
            rows.Add(row);
        }
        // Tracked stats the compare table has no row for.
        foreach (var t in options.Tracked.Where(t => !t.NativeRow))
        {
            double v0 = t.Value(b), v1 = t.Value(a);
            if (Math.Abs(v1 - v0) < 0.05) continue;
            rows.Add(new CompareRow(t.Label, Fmt(v0) + t.Unit, Fmt(v1) + t.Unit, $"{v1 - v0:+0.#;-0.#}{t.Unit}",
                v1 > v0 ? Sentiment.Better : Sentiment.Worse));
        }
        foreach (var t in options.Tracked)
        {
            double d = t.Change(b, a);
            if (Math.Abs(d) < (t.IsDamage ? 0.1 : 0.5)) continue;
            gains.Add($"{d:+0.#;-0.#}{t.Unit} {(t.IsDamage ? t.LabelFor(b.Skill) : t.Label.ToLowerInvariant())}");
        }
        // A resist that stays capped is hidden, so its thin-margin warning goes too.
        var warnings = cmp.Warnings.Where(w => !w.Contains("stays capped, but only")).ToList();
        return new UpgradeCandidate(listing, cmp, whisper, url, shortAfter, score, rows, warnings, string.Join(" · ", gains));
    }

    static Resist? ResistOf(string label, BuildStats s) => label switch
    {
        "Fire res" => s.Fire, "Cold res" => s.Cold, "Lightning res" => s.Lightning, "Chaos res" => s.Chaos, _ => null,
    };

    static string Fmt(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>
    /// A trade-site link that shows just this listing: the same search, narrowed to the seller's account and the
    /// item's base type. It's a <c>?q=</c> link, so opening it costs no API call.
    /// </summary>
    public static string? ListingUrl(JsonObject query, string league, string? account, string? baseType)
    {
        if (string.IsNullOrEmpty(account)) return null;
        var q = (JsonObject)query.DeepClone();
        var inner = (JsonObject)q["query"]!;
        if (!string.IsNullOrEmpty(baseType)) inner["type"] = baseType;
        var filters = inner["filters"] as JsonObject ?? (JsonObject)(inner["filters"] = new JsonObject());
        var trade = filters["trade_filters"] as JsonObject ?? (JsonObject)(filters["trade_filters"] = new JsonObject());
        var tf = trade["filters"] as JsonObject ?? (JsonObject)(trade["filters"] = new JsonObject());
        tf["account"] = new JsonObject { ["input"] = account };
        q["sort"] = new JsonObject { ["price"] = "asc" };
        return $"{TradeClient.BaseUrl}/trade/search/{Uri.EscapeDataString(league)}?q={Uri.EscapeDataString(q.ToJsonString())}";
    }

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
                "pseudo.pseudo_total_dexterity" => t.Dex,
                "pseudo.pseudo_total_intelligence" => t.Int,
                "pseudo.pseudo_total_mana" => t.Mana,
                "pseudo.pseudo_total_energy_shield" => t.EnergyShield,
                "pseudo.pseudo_total_life_regen" => t.LifeRegenFlat,
                "pseudo.pseudo_total_attack_speed" => t.AttackSpeedPct,
                "pseudo.pseudo_total_cast_speed" => t.CastSpeedPct,
                ChaosResId => t.Chaos,
                _ => mods.Sum(m => ModValue(m, w.Label)),
            };
            total += v * w.Weight;
        }
        return total;
    }

    /// <summary>
    /// The mod's value if its text is <paramref name="skeleton"/> (the trade site's convention: the average for
    /// "Adds # to #", negative for "-#"), else 0.
    /// </summary>
    static double ModValue(string mod, string skeleton)
    {
        var sk = TradeData.Skeleton(mod);
        var n = Items.ItemParser.Numbers(mod);
        if (n.Count == 0) return 0;
        if (sk.StartsWith("-#")) sk = "+#" + sk[2..];
        if (sk != skeleton) return 0;
        return sk.StartsWith("Adds # to #") && n.Count >= 2 ? (n[0] + n[1]) / 2 : n[0];
    }
}
