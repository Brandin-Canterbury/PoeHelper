using PoeHelper.Core.Items;

namespace PoeHelper.Core.Data;

public sealed record MatchedStat(ItemMod Mod, StatEntry Entry, double Value, string Text)
{
    public ModKind Kind => Mod.Kind;
}

/// <summary>
/// Matches item mod lines to trade stat IDs. The trade data has one ID per (text, origin), so the mod's
/// kind picks the group ("explicit", "implicit", ...) and a numbers-to-'#' skeleton picks the entry.
/// Also tries: two-line stats, "(Local)" variants on weapons/armour, and reduced↔increased / -↔+ flips.
/// </summary>
public static class StatMatcher
{
    public static string TypeFor(ModKind kind) => kind switch
    {
        ModKind.Implicit => "implicit",
        ModKind.Crafted => "crafted",
        ModKind.Fractured => "fractured",
        ModKind.Enchant => "enchant",
        ModKind.Scourge => "scourge",
        ModKind.Crucible => "crucible",
        _ => "explicit",
    };

    public static List<MatchedStat> Match(ParsedItem item, TradeData data)
    {
        var result = new List<MatchedStat>();
        bool preferLocal = item.IsWeapon || item.IsArmour;
        var mods = item.Mods;
        for (int i = 0; i < mods.Count; i++)
        {
            var mod = mods[i];
            var type = TypeFor(mod.Kind);

            // Multi-line stat: try joining with the next line first.
            if (i + 1 < mods.Count && mods[i + 1].Kind == mod.Kind)
            {
                var joined = mod.Text + "\n" + mods[i + 1].Text;
                var hit = Find(data, type, joined, preferLocal);
                if (hit is not null)
                {
                    var merged = new ItemMod
                    {
                        Text = joined, Kind = mod.Kind, Values = [.. mod.Values, .. mods[i + 1].Values],
                        Tier = mod.Tier, Affix = mod.Affix, Tags = mod.Tags, Group = mod.Group,
                    };
                    result.Add(new MatchedStat(merged, hit.Value.Entry, hit.Value.Sign * merged.Value, joined));
                    i++;
                    continue;
                }
            }

            var single = Find(data, type, mod.Text, preferLocal);
            if (single is not null)
                result.Add(new MatchedStat(mod, single.Value.Entry, single.Value.Sign * mod.Value, mod.Text));
        }
        return result;
    }

    static (StatEntry Entry, int Sign)? Find(TradeData data, string type, string text, bool preferLocal)
    {
        foreach (var (candidate, sign) in Variants(TradeData.Skeleton(text)))
        {
            if (preferLocal)
            {
                var local = data.StatsBySkeleton(type, candidate + " (Local)");
                if (local.Count > 0) return (local[0], sign);
            }
            var hits = data.StatsBySkeleton(type, candidate);
            if (hits.Count > 0) return (hits[0], sign);
        }
        return null;
    }

    static IEnumerable<(string Text, int Sign)> Variants(string skeleton)
    {
        yield return (skeleton, 1);
        if (skeleton.StartsWith("-#")) yield return ("+#" + skeleton[2..], -1);
        if (skeleton.StartsWith("#") ) yield return ("+" + skeleton, 1);
        foreach (var (a, b) in new[] { ("reduced", "increased"), ("less", "more"), ("increased", "reduced"), ("more", "less") })
            if (skeleton.Contains($" {a} ")) yield return (skeleton.Replace($" {a} ", $" {b} "), -1);
    }
}
