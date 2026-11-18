using System.Globalization;
using System.Text.Json.Nodes;
using PoeHelper.Core.Data;
using PoeHelper.Core.Items;
using PoeHelper.Core.Stats;

namespace PoeHelper.Core.Pricing;

public enum QueryKind { Search, Exchange }

/// <summary>One stat (or property) filter the search may use; the overlay lists these with tick boxes.</summary>
public sealed class FilterChoice
{
    public required string Key { get; init; }     // stat id, or "weapon:pdps" / "armour:es" / "socket:links"
    public required string Label { get; init; }   // what the overlay shows
    public double? Min { get; set; }
    public double? Max { get; set; }
    public bool Enabled { get; set; }
    public int? Tier { get; init; }
}

public sealed class PriceQuery
{
    public QueryKind Kind { get; init; }
    public required ParsedItem Item { get; init; }
    public string? ExchangeId { get; init; }
    public string? Type { get; set; }
    public string? Discriminator { get; set; }
    public string? Name { get; set; }
    public string? Category { get; set; }
    public string? RarityOption { get; set; }
    public List<FilterChoice> Filters { get; } = [];
    public Dictionary<string, (double? Min, double? Max, string? Option)> Misc { get; } = new();
    public string Status { get; set; } = "securable";

    public JsonObject ToSearchJson()
    {
        var q = new JsonObject { ["status"] = new JsonObject { ["option"] = Status } };
        if (Name is not null) q["name"] = Name;
        if (Type is not null)
            q["type"] = Discriminator is null ? Type : new JsonObject { ["option"] = Type, ["discriminator"] = Discriminator };

        var filters = new JsonObject();
        var typeFilters = new JsonObject();
        if (Category is not null) typeFilters["category"] = new JsonObject { ["option"] = Category };
        if (RarityOption is not null) typeFilters["rarity"] = new JsonObject { ["option"] = RarityOption };
        if (typeFilters.Count > 0) filters["type_filters"] = new JsonObject { ["filters"] = typeFilters };

        var groups = new Dictionary<string, JsonObject>();
        JsonObject Group(string name) => groups.TryGetValue(name, out var g) ? g : groups[name] = new JsonObject();

        foreach (var (key, (min, max, option)) in Misc)
        {
            var parts = key.Split(':'); // "misc_filters:gem_level"
            Group(parts[0])[parts[1]] = Range(min, max, option);
        }

        var stats = new JsonArray();
        foreach (var f in Filters.Where(f => f.Enabled))
        {
            if (f.Key.Contains(':'))
            {
                var parts = f.Key.Split(':');
                Group(parts[0])[parts[1]] = Range(f.Min, f.Max, null);
            }
            else
            {
                // Option stats (anoints, cluster notables) come as "enchant.stat_X|nodeId".
                var id = f.Key;
                var value = new JsonObject();
                int bar = id.IndexOf('|');
                if (bar > 0) { value["option"] = id[(bar + 1)..]; id = id[..bar]; }
                else
                {
                    if (f.Min is { } mn) value["min"] = mn;
                    if (f.Max is { } mx) value["max"] = mx;
                }
                stats.Add(new JsonObject { ["id"] = id, ["value"] = value, ["disabled"] = false });
            }
        }
        foreach (var (name, g) in groups) filters[name] = new JsonObject { ["filters"] = g };
        if (filters.Count > 0) q["filters"] = filters;
        q["stats"] = new JsonArray { new JsonObject { ["type"] = "and", ["filters"] = stats } };

        return new JsonObject { ["query"] = q, ["sort"] = new JsonObject { ["price"] = "asc" } };
    }

    public JsonObject ToExchangeJson() => new()
    {
        ["query"] = new JsonObject
        {
            ["status"] = new JsonObject { ["option"] = "online" },
            ["have"] = new JsonArray("chaos", "divine"),
            ["want"] = new JsonArray(ExchangeId),
        },
        ["sort"] = new JsonObject { ["have"] = "asc" },
        ["engine"] = "new",
    };

    static JsonObject Range(double? min, double? max, string? option)
    {
        var o = new JsonObject();
        if (option is not null) o["option"] = option;
        if (min is { } mn) o["min"] = mn;
        if (max is { } mx) o["max"] = mx;
        return o;
    }
}

/// <summary>Turns a parsed item into a "find things like this" query, choosing stats the way a trader would.</summary>
public static class PriceQueryBuilder
{
    public const int MaxEnabledStats = 7;
    const double RollFloor = 0.9; // search for ≥90% of each rolled value

    public static PriceQuery Build(ParsedItem item, TradeData data, string status = "securable")
    {
        // Bulk items (currency, cards, fragments, scarabs, essences, oils...) go to the exchange.
        if (item.Rarity is not (ItemRarity.Unique or ItemRarity.Rare or ItemRarity.Magic or ItemRarity.Gem)
            && !item.IsMap && data.ExchangeId(item.Name ?? item.BaseType) is { } slug)
            return new PriceQuery { Kind = QueryKind.Exchange, Item = item, ExchangeId = slug };

        var q = new PriceQuery { Kind = QueryKind.Search, Item = item, Status = status };
        switch (item.Rarity)
        {
            case ItemRarity.Gem: BuildGem(item, data, q); break;
            case ItemRarity.Unique: BuildUnique(item, data, q); break;
            case ItemRarity.DivinationCard or ItemRarity.Currency:
                q.Type = item.Name; break;
            default:
                if (item.IsMap) BuildMap(item, q);
                else BuildEquipment(item, data, q);
                break;
        }
        return q;
    }

    static void BuildGem(ParsedItem item, TradeData data, PriceQuery q)
    {
        var name = item.Name ?? item.BaseType;
        if (data.FindGem(name) is { } transfigured)
        {
            q.Type = transfigured.Type;
            q.Discriminator = transfigured.Disc;
        }
        else q.Type = name;

        // Max-level gems are priced exactly; levelling gems at "this level or better" (the floor price).
        bool maxed = item.GemLevel >= 20 || item.Properties.GetValueOrDefault("Level", "").Contains("(Max)");
        if (item.GemLevel > 0) q.Misc["misc_filters:gem_level"] = (item.GemLevel, maxed ? item.GemLevel : null, null);
        if (item.Quality > 0) q.Misc["misc_filters:quality"] = (item.Quality, null, null);
        q.Misc["misc_filters:corrupted"] = (null, null, item.Corrupted ? "true" : "false");
    }

    static void BuildUnique(ParsedItem item, TradeData data, PriceQuery q)
    {
        if (!item.Identified || item.Name is null)
        {
            q.Type = item.BaseType;
            q.RarityOption = "unique";
        }
        else
        {
            var entry = data.FindUnique(item.Name, item.BaseType);
            q.Name = item.Name;
            q.Type = entry?.Type ?? item.BaseType;
        }
        q.Misc["misc_filters:corrupted"] = (null, null, item.Corrupted ? "true" : "false");
        AddLinks(item, q);

        // Variable-roll uniques: offer every explicit roll as a tick box, enabled for none by default.
        foreach (var m in StatMatcher.Match(item, data).Where(m => m.Kind is ModKind.Explicit && m.Mod.Values.Count > 0))
            q.Filters.Add(new FilterChoice { Key = m.Entry.Id, Label = m.Text.Replace('\n', ' '), Min = Floor(m.Value), Enabled = false });
    }

    static void BuildMap(ParsedItem item, PriceQuery q)
    {
        q.Type = item.Rarity == ItemRarity.Unique ? item.BaseType : item.BaseType.Replace("Blighted ", "").Replace("Blight-ravaged ", "");
        if (item.MapTier > 0) q.Misc["map_filters:map_tier"] = (item.MapTier, item.MapTier, null);
        if (item.BaseType.StartsWith("Blighted ")) q.Misc["map_filters:map_blighted"] = (null, null, "true");
        if (item.BaseType.StartsWith("Blight-ravaged ")) q.Misc["map_filters:map_uberblighted"] = (null, null, "true");
    }

    static void BuildEquipment(ParsedItem item, TradeData data, PriceQuery q)
    {
        if (item.TypeLine is not null) item.BaseType = data.ResolveBaseType(item.TypeLine);
        q.Category = ItemCategories.ToTradeCategory(item.ItemClass);
        q.RarityOption = "nonunique";

        // Base type matters for jewellery, jewels and flasks (implicits come from the base);
        // for normal/magic gear the base *is* the value (crafting bases).
        if (item.IsAccessory || item.IsJewel || item.IsFlask || item.Rarity is ItemRarity.Normal || q.Category is null)
            q.Type = item.BaseType;

        if (item.Rarity == ItemRarity.Normal || !item.Identified)
        {
            if (item.ItemLevel > 0) q.Misc["misc_filters:ilvl"] = (item.ItemLevel, null, null);
            foreach (var inf in item.Influences)
                AddInfluence(inf, q);
            AddLinks(item, q);
            return;
        }

        AddLinks(item, q);
        AddDefenceAndDps(item, q);

        var matched = StatMatcher.Match(item, data);
        var countable = matched.Where(m => m.Kind is not ModKind.Crafted).ToList();
        var totals = StatTotals.FromMods(countable.Select(m => m.Mod));

        var pseudo = new List<FilterChoice>();
        void Pseudo(string id, string label, double value, double threshold)
        {
            if (value >= threshold)
                pseudo.Add(new FilterChoice { Key = id, Label = $"{label} ({value:0})", Min = Floor(value), Enabled = true });
        }
        Pseudo("pseudo.pseudo_total_life", "Total maximum Life", totals.Life, 30);
        Pseudo("pseudo.pseudo_total_elemental_resistance", "Total Elemental Resistance", totals.TotalElementalRes, 30);
        Pseudo("pseudo.pseudo_total_chaos_resistance", "Total Chaos Resistance", totals.Chaos, 10);
        Pseudo("pseudo.pseudo_total_energy_shield", "Total maximum Energy Shield", item.IsArmour ? 0 : totals.EnergyShield, 25);
        Pseudo("pseudo.pseudo_total_strength", "Total Strength", totals.Str, 25);
        Pseudo("pseudo.pseudo_total_dexterity", "Total Dexterity", totals.Dex, 25);
        Pseudo("pseudo.pseudo_total_intelligence", "Total Intelligence", totals.Int, 25);
        // Attributes are cheap filler on most items: list them, but only search on them when nothing else counts.
        foreach (var p in pseudo.Where(p => p.Key.Contains("strength") || p.Key.Contains("dexterity") || p.Key.Contains("intelligence")))
            p.Enabled = false;

        var others = new List<FilterChoice>();
        foreach (var m in matched)
        {
            if (IsCoveredByPseudo(m.Mod)) continue;
            bool baseImplicit = m.Kind == ModKind.Implicit && q.Type is not null; // base type already fixes it
            bool important = m.Kind is ModKind.Enchant or ModKind.Fractured
                             || m.Mod.Tier is null or <= 2
                             || (m.Mod.Tags?.Contains("Damage") ?? false) && m.Mod.Tier <= 3;
            others.Add(new FilterChoice
            {
                Key = m.Entry.Id,
                Label = m.Text.Replace('\n', ' ') + (m.Mod.Tier is { } t ? $"  [T{t}]" : "") + (m.Kind == ModKind.Crafted ? "  (crafted)" : ""),
                Min = m.Value > 0 ? Floor(m.Value) : null,
                Max = m.Value < 0 ? Math.Ceiling(m.Value / RollFloor) : null,
                Enabled = important && !baseImplicit && m.Kind != ModKind.Crafted && !(m.Mod.Tier is null && m.Value < 0),
                Tier = m.Mod.Tier,
            });
        }

        q.Filters.AddRange(pseudo);
        q.Filters.AddRange(others.OrderBy(o => o.Tier ?? 99));

        // Too many filters ⇒ zero results. Keep the strongest ones on.
        int enabled = 0;
        foreach (var f in q.Filters)
        {
            if (!f.Enabled) continue;
            if (++enabled > MaxEnabledStats) f.Enabled = false;
        }
        // Nothing worth searching on (e.g. only attributes): fall back to those.
        if (!q.Filters.Any(f => f.Enabled && !f.Key.Contains(':')))
            foreach (var f in pseudo) f.Enabled = true;
    }

    static void AddLinks(ParsedItem item, PriceQuery q)
    {
        if (item.MaxLinks >= 5)
            q.Filters.Add(new FilterChoice { Key = "socket_filters:links", Label = $"{item.MaxLinks}-link", Min = item.MaxLinks, Enabled = true });
    }

    static void AddDefenceAndDps(ParsedItem item, PriceQuery q)
    {
        if (item.IsArmour)
        {
            foreach (var (prop, key) in new[] { ("Energy Shield", "es"), ("Armour", "ar"), ("Evasion Rating", "ev"), ("Ward", "ward") })
            {
                if (!item.Properties.TryGetValue(prop, out var raw)) continue;
                int v = ItemParser.FirstInt(raw);
                if (v <= 0) continue;
                bool big = raw.Contains("augmented") || v >= 150;
                q.Filters.Add(new FilterChoice { Key = $"armour_filters:{key}", Label = $"{prop} {v}", Min = Floor(v), Enabled = big && key == "es" });
            }
        }
        if (item.IsWeapon)
        {
            var (pdps, edps) = WeaponDps(item);
            if (pdps > 0) q.Filters.Add(new FilterChoice { Key = "weapon_filters:pdps", Label = $"Physical DPS {pdps:0}", Min = Floor(pdps), Enabled = pdps >= 150 });
            if (edps > 0) q.Filters.Add(new FilterChoice { Key = "weapon_filters:edps", Label = $"Elemental DPS {edps:0}", Min = Floor(edps), Enabled = edps >= 150 });
        }
    }

    public static (double Pdps, double Edps) WeaponDps(ParsedItem item)
    {
        double aps = item.Properties.TryGetValue("Attacks per Second", out var a) ? Parse(a.Split(' ')[0]) : 0;
        double Avg(string key) => item.Properties.TryGetValue(key, out var v)
            ? v.Split(',').Select(r => ItemParser.Numbers(r)).Where(n => n.Count >= 2).Sum(n => (n[0] + n[1]) / 2)
            : 0;
        return (Avg("Physical Damage") * aps, (Avg("Elemental Damage") + Avg("Chaos Damage") * 0) * aps);
    }

    static bool IsCoveredByPseudo(ItemMod mod)
    {
        var t = new StatTotals();
        if (!t.Add(mod.Text, mod.Values)) return false;
        return t.Life != 0 || t.Fire != 0 || t.Cold != 0 || t.Lightning != 0 || t.Chaos != 0
               || t.Str != 0 || t.Dex != 0 || t.Int != 0;
    }

    static void AddInfluence(string influence, PriceQuery q)
    {
        switch (influence)
        {
            case "Searing Exarch": q.Misc["misc_filters:searing_item"] = (null, null, "true"); break;
            case "Eater of Worlds": q.Misc["misc_filters:tangled_item"] = (null, null, "true"); break;
            default:
                q.Filters.Add(new FilterChoice
                {
                    Key = $"pseudo.pseudo_has_{influence.ToLowerInvariant()}_influence",
                    Label = $"{influence} influence", Enabled = true,
                });
                break;
        }
    }

    static double Floor(double v) => Math.Floor(v * RollFloor);
    static double Parse(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
}
