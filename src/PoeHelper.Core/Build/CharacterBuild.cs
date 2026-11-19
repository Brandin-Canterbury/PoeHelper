using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PoeHelper.Core.Items;
using PoeHelper.Core.Trade;

namespace PoeHelper.Core.Build;

/// <param name="Tags">The gem's tag line, e.g. "Spell, AoE, Fire".</param>
/// <param name="Group">The socket group (link group) it sits in on its item.</param>
/// <param name="Quality">Gem quality in %.</param>
public sealed record GemInfo(string Name, int Level, int Str, int Dex, int Int, IReadOnlyList<string> Mods, bool Support,
    IReadOnlyList<string>? Tags = null, int Group = 0, int Quality = 0);

/// <summary>One equipped item, reduced to what the model needs.</summary>
public sealed class EquippedItem
{
    public required string Slot { get; init; }
    public required string Name { get; init; }
    public string ItemClass { get; init; } = "";
    public List<string> Mods { get; init; } = [];
    public int ReqStr { get; init; }
    public int ReqDex { get; init; }
    public int ReqInt { get; init; }
    public int SocketCount { get; init; }
    public int MaxLinks { get; init; }
    public List<GemInfo> Gems { get; init; } = [];
    /// <summary>Property block as "name → value" (e.g. "Physical Damage" → "50-76", "Attacks per Second" → "1.25").</summary>
    public Dictionary<string, string> Props { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The trade-site JSON for this item, kept for building upgrade searches.</summary>
    public JsonObject? Raw { get; init; }
}

/// <summary>Everything about the character that the compare model uses, from the public character API.</summary>
public sealed partial class CharacterBuild
{
    public static readonly string[] GearSlots = ["Weapon", "Offhand", "Helm", "BodyArmour", "Gloves", "Boots", "Belt", "Amulet", "Ring", "Ring2"];

    public string Name { get; init; } = "";
    public string ClassName { get; init; } = "";
    public string League { get; init; } = "";
    public int Level { get; init; }
    public int BaseStr { get; init; }
    public int BaseDex { get; init; }
    public int BaseInt { get; init; }
    public Dictionary<string, EquippedItem> Items { get; init; } = new();
    /// <summary>Stat lines from allocated passives, masteries and anoints.</summary>
    public List<string> TreeStats { get; init; } = [];
    public List<string> JewelMods { get; init; } = [];
    public DateTime LoadedAt { get; init; } = DateTime.UtcNow;

    string? _mainSkillOverride;
    SkillProfile? _skill;
    bool _skillDetected;

    /// <summary>Gem name (or part of it) to treat as the main skill; blank = detect from the links.</summary>
    public string? MainSkillOverride
    {
        get => _mainSkillOverride;
        set { _mainSkillOverride = value; _skillDetected = false; }
    }

    /// <summary>The build's main skill, detected once from the character as loaded (swaps don't change it).</summary>
    public SkillProfile? Skill
    {
        get
        {
            if (!_skillDetected) { _skill = MainSkill.Detect(this, _mainSkillOverride); _skillDetected = true; }
            return _skill;
        }
    }

    public CharacterBuild WithItem(string slot, EquippedItem? item)
    {
        var items = new Dictionary<string, EquippedItem>(Items);
        if (item is null) items.Remove(slot); else items[slot] = item;
        var skill = Skill;
        return new CharacterBuild
        {
            Name = Name, ClassName = ClassName, League = League, Level = Level,
            BaseStr = BaseStr, BaseDex = BaseDex, BaseInt = BaseInt,
            Items = items, TreeStats = TreeStats, JewelMods = JewelMods, LoadedAt = LoadedAt,
            _mainSkillOverride = _mainSkillOverride, _skill = skill, _skillDetected = true,
        };
    }

    public static async Task<CharacterBuild> LoadAsync(TradeClient client, string account, string character, string cacheDir, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string> { ["accountName"] = account, ["character"] = character, ["realm"] = "pc" };
        var items = (await client.CharacterWindowAsync("get-items", form, ct)).AsObject();
        var passives = (await client.CharacterWindowAsync("get-passive-skills", form, ct)).AsObject();
        var tree = await PassiveTree.LoadAsync(cacheDir, ct);
        return FromJson(items, passives, tree);
    }

    public static CharacterBuild FromJson(JsonObject itemsJson, JsonObject passivesJson, PassiveTree tree)
    {
        var ch = itemsJson["character"]!;
        string cls = ch["class"]?.GetValue<string>() ?? "";
        int classId = passivesJson["character"]?.GetValue<int>() ?? -1;
        var (bs, bd, bi, baseClass) = tree.ClassAttributes(classId, cls);

        var equipped = new Dictionary<string, EquippedItem>();
        foreach (var node in itemsJson["items"]!.AsArray())
        {
            var it = node!.AsObject();
            var slot = it["inventoryId"]?.GetValue<string>() ?? "";
            if (!GearSlots.Contains(slot)) continue;
            equipped[slot] = FromApiItem(slot, it);
        }

        var treeStats = new List<string>();
        foreach (var h in passivesJson["hashes"]!.AsArray()) treeStats.AddRange(tree.NodeStats(h!.GetValue<int>()));
        foreach (var h in passivesJson["hashes_ex"]?.AsArray() ?? []) treeStats.AddRange(tree.NodeStats(h!.GetValue<int>()));
        foreach (var (nodeId, effect) in passivesJson["mastery_effects"]?.AsObject() ?? [])
            treeStats.AddRange(tree.MasteryStats(int.Parse(nodeId), effect!.GetValue<int>()));
        // Anoints allocate a notable: "Allocates [golem's_blood1088|Golem's Blood]".
        foreach (var it in equipped.Values)
            foreach (var m in it.Mods.Where(m => m.StartsWith("Allocates ")))
                treeStats.AddRange(tree.NotableStats(m["Allocates ".Length..]));

        var jewelMods = new List<string>();
        foreach (var j in passivesJson["items"]?.AsArray() ?? [])
            jewelMods.AddRange(ModLines(j!.AsObject()));

        return new CharacterBuild
        {
            Name = ch["name"]?.GetValue<string>() ?? "",
            ClassName = cls,
            League = ch["league"]?.GetValue<string>() ?? "",
            Level = ch["level"]?.GetValue<int>() ?? 1,
            BaseStr = bs, BaseDex = bd, BaseInt = bi,
            Items = equipped, TreeStats = treeStats, JewelMods = jewelMods,
        };
    }

    public static EquippedItem FromApiItem(string slot, JsonObject it)
    {
        var (str, dex, intl) = Requirements(it);
        var sockets = it["sockets"]?.AsArray();
        int links = sockets is { Count: > 0 } ? sockets.GroupBy(s => s?["group"]?.GetValue<int>()).Max(g => g.Count()) : 0;
        var gems = new List<GemInfo>();
        foreach (var g in it["socketedItems"]?.AsArray() ?? [])
        {
            var go = g!.AsObject();
            if (go["abyssJewel"]?.GetValue<bool>() == true) continue;
            var (gs, gd, gi) = Requirements(go);
            var level = go["properties"]?.AsArray().FirstOrDefault(p => p?["name"]?.GetValue<string>() == "Level")
                ?["values"]?[0]?[0]?.GetValue<string>() ?? "0";
            var quality = go["properties"]?.AsArray().FirstOrDefault(p => p?["name"]?.GetValue<string>() == "Quality")
                ?["values"]?[0]?[0]?.GetValue<string>() ?? "0";
            var tags = (go["properties"]?.AsArray().FirstOrDefault()?["name"]?.GetValue<string>() ?? "")
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            int socket = go["socket"]?.GetValue<int>() ?? -1;
            int group = sockets is not null && socket >= 0 && socket < sockets.Count ? sockets[socket]?["group"]?.GetValue<int>() ?? 0 : 0;
            gems.Add(new GemInfo(go["typeLine"]?.GetValue<string>() ?? "?", ItemParser.FirstInt(level), gs, gd, gi,
                ModLines(go), go["support"]?.GetValue<bool>() == true, tags, group, ItemParser.FirstInt(quality)));
        }
        string name = it["name"]?.GetValue<string>() is { Length: > 0 } n ? $"{n} {it["typeLine"]}" : it["typeLine"]?.GetValue<string>() ?? "?";
        return new EquippedItem
        {
            Slot = slot, Name = StripMarkup(name), Mods = ModLines(it), ReqStr = str, ReqDex = dex, ReqInt = intl,
            SocketCount = sockets?.Count ?? 0, MaxLinks = links, Gems = gems, Raw = it, Props = ApiProps(it),
        };
    }

    /// <summary>"Physical Damage" → "50-76"; "Elemental Damage" → "10-20, 5-60" (all values joined).</summary>
    static Dictionary<string, string> ApiProps(JsonObject it)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in it["properties"]?.AsArray() ?? [])
        {
            var name = p?["name"]?.GetValue<string>();
            var values = p?["values"]?.AsArray();
            if (string.IsNullOrEmpty(name) || values is not { Count: > 0 }) continue;
            d[StripMarkup(name)] = string.Join(", ", values.Select(v => v?[0]?.GetValue<string>() ?? ""));
        }
        return d;
    }

    /// <summary>Implicit, explicit, crafted, enchant, fractured mods as plain lines (the API sends strings or {description}).</summary>
    public static List<string> ModLines(JsonObject it)
    {
        var lines = new List<string>();
        foreach (var key in new[] { "implicitMods", "explicitMods", "craftedMods", "enchantMods", "fracturedMods", "scourgeMods", "crucibleMods" })
            foreach (var m in it[key]?.AsArray() ?? [])
            {
                string? text = m is JsonValue v ? v.GetValue<string>() : m?["description"]?.GetValue<string>();
                if (text is null) continue;
                foreach (var line in text.Split('\n')) lines.Add(StripMarkup(line));
            }
        return lines;
    }

    static (int Str, int Dex, int Int) Requirements(JsonObject it)
    {
        int s = 0, d = 0, i = 0;
        foreach (var r in it["requirements"]?.AsArray() ?? [])
        {
            var name = r?["name"]?.GetValue<string>();
            int v = ItemParser.FirstInt(r?["values"]?[0]?[0]?.GetValue<string>() ?? "0");
            switch (name)
            {
                case "Str" or "Strength": s = v; break;
                case "Dex" or "Dexterity": d = v; break;
                case "Int" or "Intelligence": i = v; break;
            }
        }
        return (s, d, i);
    }

    /// <summary>"[golem's_blood1088|Golem's Blood]" → "Golem's Blood"; "[Ignite]" → "Ignite".</summary>
    public static string StripMarkup(string s) => Markup().Replace(s, m => m.Groups[2].Success ? m.Groups[2].Value : m.Groups[1].Value);

    [GeneratedRegex(@"\[([^\]|]+)(?:\|([^\]]+))?\]")]
    private static partial Regex Markup();
}
