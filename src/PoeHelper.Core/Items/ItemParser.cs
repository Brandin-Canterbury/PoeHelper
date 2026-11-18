using System.Globalization;
using System.Text.RegularExpressions;

namespace PoeHelper.Core.Items;

/// <summary>
/// Parses the text PoE puts on the clipboard for Ctrl+C (plain) and Ctrl+Alt+C (advanced, with mod tiers).
/// Unknown lines are kept as candidate mods; the stat index decides later which ones are real.
/// </summary>
public static partial class ItemParser
{
    const string Separator = "--------";

    static readonly string[] FlagLines =
    [
        "Corrupted", "Mirrored", "Unidentified", "Split", "Fractured Item", "Synthesised Item",
        "Shaper Item", "Elder Item", "Crusader Item", "Hunter Item", "Redeemer Item", "Warlord Item",
        "Searing Exarch Item", "Eater of Worlds Item", "Unmodifiable",
    ];

    static readonly (string Suffix, ModKind Kind)[] KindSuffixes =
    [
        (" (implicit)", ModKind.Implicit),
        (" (crafted)", ModKind.Crafted),
        (" (fractured)", ModKind.Fractured),
        (" (enchant)", ModKind.Enchant),
        (" (scourge)", ModKind.Scourge),
        (" (crucible)", ModKind.Crucible),
    ];

    public static bool LooksLikeItem(string? text) =>
        !string.IsNullOrWhiteSpace(text) && (text.StartsWith("Item Class:") || text.StartsWith("Rarity:"));

    public static ParsedItem Parse(string text)
    {
        if (!LooksLikeItem(text)) throw new FormatException("Clipboard does not contain a Path of Exile item.");

        var item = new ParsedItem { RawText = text };
        var sections = SplitSections(text);
        ParseHeader(sections[0], item);

        int group = 0;
        bool sawItemLevel = false;
        for (int s = 1; s < sections.Count; s++)
        {
            var lines = sections[s];
            if (lines.Count == 0) continue;
            var first = lines[0];

            if (first == "Requirements:") { ParseRequirements(lines, item); continue; }
            if (first.StartsWith("Sockets:")) { ParseSockets(first, item); continue; }
            if (first.StartsWith("Item Level:")) { item.ItemLevel = FirstInt(first); sawItemLevel = true; continue; }
            if (first.StartsWith("Note:")) { item.Note = first["Note:".Length..].Trim(); continue; }
            if (lines.All(l => FlagLines.Contains(l))) { ApplyFlags(lines, item); continue; }
            if (first.StartsWith("Stack Size:")) { item.StackSize = ParseStack(first); continue; }

            bool isModSection = lines.Any(l => l.StartsWith('{') || KindSuffixes.Any(k => l.EndsWith(k.Suffix)));
            if (!isModSection && s == 1)
            {
                ParseProperties(lines, item);
                continue;
            }

            // Gems, currency and cards carry descriptive text, not mods.
            if (item.Rarity is ItemRarity.Gem or ItemRarity.Currency or ItemRarity.DivinationCard) continue;
            // Before "Item Level" only implicit/enchant style sections are mods; skip stray text.
            if (!isModSection && !sawItemLevel && item.Rarity != ItemRarity.Unique) continue;

            ParseModSection(lines, item, ref group);
        }

        if (item.Rarity == ItemRarity.Gem && item.Properties.TryGetValue("Level", out var lvl))
            item.GemLevel = FirstInt(lvl);
        if (item.Properties.TryGetValue("Quality", out var q)) item.Quality = FirstInt(q);
        if (item.Properties.TryGetValue("Map Tier", out var mt)) item.MapTier = FirstInt(mt);
        if (item.Properties.TryGetValue("Stack Size", out var ss)) item.StackSize = ParseStack("Stack Size: " + ss);
        return item;
    }

    static List<List<string>> SplitSections(string text)
    {
        var result = new List<List<string>> { new() };
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line == Separator) { result.Add([]); continue; }
            if (line.Length == 0) continue;
            result[^1].Add(line);
        }
        result.RemoveAll(s => s.Count == 0 && result.IndexOf(s) != 0);
        return result;
    }

    static void ParseHeader(List<string> lines, ParsedItem item)
    {
        var names = new List<string>();
        foreach (var line in lines)
        {
            if (line.StartsWith("Item Class:")) item.ItemClass = line["Item Class:".Length..].Trim();
            else if (line.StartsWith("Rarity:")) item.Rarity = ParseRarity(line["Rarity:".Length..].Trim());
            else names.Add(line);
        }

        switch (item.Rarity)
        {
            case ItemRarity.Rare or ItemRarity.Unique when names.Count >= 2:
                item.Name = names[0];
                item.BaseType = StripBasePrefixes(names[1], item);
                break;
            case ItemRarity.Gem or ItemRarity.Currency or ItemRarity.DivinationCard:
                item.Name = names.FirstOrDefault() ?? "";
                item.BaseType = item.Name;
                break;
            default:
                // Normal, magic, unidentified rare/unique: one line. Magic affixes are stripped later
                // against the base-type list (TradeData.ResolveBaseType).
                item.TypeLine = names.FirstOrDefault() ?? "";
                item.BaseType = StripBasePrefixes(item.TypeLine, item);
                break;
        }
    }

    static string StripBasePrefixes(string line, ParsedItem item)
    {
        foreach (var prefix in new[] { "Superior ", "Synthesised " })
        {
            if (!line.StartsWith(prefix)) continue;
            if (prefix == "Synthesised ") item.Synthesised = true;
            line = line[prefix.Length..];
        }
        return line;
    }

    static ItemRarity ParseRarity(string s) => s switch
    {
        "Normal" => ItemRarity.Normal,
        "Magic" => ItemRarity.Magic,
        "Rare" => ItemRarity.Rare,
        "Unique" => ItemRarity.Unique,
        "Gem" => ItemRarity.Gem,
        "Currency" => ItemRarity.Currency,
        "Divination Card" => ItemRarity.DivinationCard,
        _ => ItemRarity.Other,
    };

    static void ParseRequirements(List<string> lines, ParsedItem item)
    {
        foreach (var line in lines.Skip(1))
        {
            var (key, value) = SplitProperty(line);
            int v = FirstInt(value);
            switch (key)
            {
                case "Level": item.Requirements.Level = v; break;
                case "Str" or "Strength": item.Requirements.Str = v; break;
                case "Dex" or "Dexterity": item.Requirements.Dex = v; break;
                case "Int" or "Intelligence": item.Requirements.Int = v; break;
            }
        }
    }

    static void ParseSockets(string line, ParsedItem item)
    {
        var s = line["Sockets:".Length..].Trim();
        item.Sockets = s;
        var groups = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        item.SocketCount = groups.Sum(g => g.Split('-').Length);
        item.MaxLinks = groups.Length == 0 ? 0 : groups.Max(g => g.Split('-').Length);
    }

    static void ApplyFlags(List<string> lines, ParsedItem item)
    {
        foreach (var l in lines)
        {
            switch (l)
            {
                case "Corrupted": item.Corrupted = true; break;
                case "Mirrored": item.Mirrored = true; break;
                case "Unidentified": item.Identified = false; break;
                case "Split": item.Split = true; break;
                case "Fractured Item": item.Fractured = true; break;
                case "Synthesised Item": item.Synthesised = true; break;
                default:
                    if (l.EndsWith(" Item")) item.Influences.Add(l[..^" Item".Length]);
                    break;
            }
        }
    }

    static void ParseProperties(List<string> lines, ParsedItem item)
    {
        foreach (var line in lines)
        {
            if (!line.Contains(": ")) continue;
            var (key, value) = SplitProperty(line);
            item.Properties[key] = value;
        }
    }

    static void ParseModSection(List<string> lines, ParsedItem item, ref int group)
    {
        ModHeader? header = null;
        foreach (var raw in lines)
        {
            if (raw.StartsWith('{'))
            {
                item.AdvancedText = true;
                header = ParseModHeader(raw);
                group++;
                continue;
            }
            // Advanced copy adds reminder text like "(Shock increases Damage taken by ...)".
            if (raw.StartsWith('(') && raw.EndsWith(')')) continue;
            if (header is null) group++;

            var line = raw;
            var kind = header?.Kind ?? ModKind.Explicit;
            foreach (var (suffix, k) in KindSuffixes)
            {
                if (!line.EndsWith(suffix)) continue;
                line = line[..^suffix.Length];
                kind = k;
                break;
            }
            line = line.Replace(" — Unscalable Value", "");
            line = RangeAnnotation().Replace(line, "$1");

            item.Mods.Add(new ItemMod
            {
                Text = line,
                Kind = kind,
                Values = Numbers(line),
                Tier = header?.Tier,
                Affix = header?.Affix,
                Tags = header?.Tags,
                Group = group,
            });
        }
    }

    sealed record ModHeader(ModKind Kind, int? Tier, string? Affix, string? Tags);

    static ModHeader ParseModHeader(string line)
    {
        var inner = line.Trim('{', '}', ' ');
        string? tags = null;
        int dash = inner.IndexOf(" — ", StringComparison.Ordinal);
        if (dash >= 0) { tags = inner[(dash + 3)..].Trim(); inner = inner[..dash]; }

        var kind = inner switch
        {
            _ when inner.StartsWith("Implicit") || inner.Contains("Eldritch") || inner.Contains("Corruption Implicit") => ModKind.Implicit,
            _ when inner.Contains("Master Crafted") => ModKind.Crafted,
            _ when inner.StartsWith("Fractured") => ModKind.Fractured,
            _ when inner.Contains("Enchant") => ModKind.Enchant,
            _ => ModKind.Explicit,
        };
        int? tier = null;
        var m = TierPattern().Match(inner);
        if (m.Success) tier = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        string? affix = inner.Contains("Prefix") ? "Prefix" : inner.Contains("Suffix") ? "Suffix" : null;
        return new ModHeader(kind, tier, affix, tags);
    }

    static (string Key, string Value) SplitProperty(string line)
    {
        int i = line.IndexOf(": ", StringComparison.Ordinal);
        return i < 0 ? (line.Trim(), "") : (line[..i].Trim(), line[(i + 2)..].Trim());
    }

    static int ParseStack(string line)
    {
        var v = line["Stack Size:".Length..].Split('/')[0].Replace(",", "").Replace(".", "").Trim();
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 1;
    }

    public static int FirstInt(string s)
    {
        var m = IntPattern().Match(s);
        return m.Success ? int.Parse(m.Value, CultureInfo.InvariantCulture) : 0;
    }

    public static IReadOnlyList<double> Numbers(string s) =>
        NumberPattern().Matches(s).Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();

    /// <summary>"95(90-99)" → "95"; handles negatives like "-10(-15--10)".</summary>
    [GeneratedRegex(@"(-?\d+(?:\.\d+)?)\((?:-?\d+(?:\.\d+)?)-(?:-?\d+(?:\.\d+)?)\)")]
    private static partial Regex RangeAnnotation();

    [GeneratedRegex(@"\((?:Tier|Rank): (\d+)\)")]
    private static partial Regex TierPattern();

    [GeneratedRegex(@"-?\d+")]
    private static partial Regex IntPattern();

    [GeneratedRegex(@"-?\d+(?:\.\d+)?")]
    private static partial Regex NumberPattern();
}
