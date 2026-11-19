using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoeHelper.Core.Data;

/// <summary>
/// Gem data imported from Path of Building Community (MIT licence) by tools/pob-import/import_pob.py:
/// per-level base damage, cast time, crit, effectiveness, and the modifiers each gem (active or support) applies.
/// </summary>
public sealed class SkillData
{
    [JsonPropertyName("source")] public string Source { get; init; } = "";
    [JsonPropertyName("skills")] public Dictionary<string, GemData> Skills { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    static readonly Lazy<SkillData> _instance = new(Load);
    public static SkillData Instance => _instance.Value;

    static SkillData Load()
    {
        using var s = typeof(SkillData).Assembly.GetManifestResourceStream("PoeHelper.Core.Data.skills.json")
                      ?? throw new InvalidOperationException("skills.json is not embedded");
        var d = JsonSerializer.Deserialize<SkillData>(s) ?? new SkillData();
        var byName = new Dictionary<string, GemData>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in d.Skills) byName.TryAdd(k, v); // a few PoB names differ only in case
        return new SkillData { Source = d.Source, Skills = byName };
    }

    /// <summary>A gem by its in-game name ("Fireball", "Elemental Focus Support", "Righteous Fire of Arcane Devotion").</summary>
    public GemData? Get(string name) => Skills.GetValueOrDefault(name.Trim());
}

public sealed class GemData
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("types")] public List<string> Types { get; init; } = [];
    [JsonPropertyName("support")] public bool Support { get; init; }
    [JsonPropertyName("castTime")] public double? CastTime { get; init; }
    [JsonPropertyName("flags")] public List<string> Flags { get; init; } = [];
    [JsonPropertyName("skill")] public Dictionary<string, double> Skill { get; init; } = [];
    [JsonPropertyName("mods")] public List<GemMod> Mods { get; init; } = [];
    /// <summary>Modifiers per 1% quality.</summary>
    [JsonPropertyName("quality")] public List<GemMod> Quality { get; init; } = [];
    [JsonPropertyName("levels")] public Dictionary<string, GemLevel> Levels { get; init; } = [];

    public bool HasFlag(string f) => Flags.Contains(f, StringComparer.OrdinalIgnoreCase);
    public bool HasType(string t) => Types.Contains(t, StringComparer.OrdinalIgnoreCase);

    /// <summary>The data for a gem level: that level, or the nearest one below it (the lowest if none).</summary>
    public GemLevel? Level(int level)
    {
        GemLevel? best = null;
        int bestLvl = int.MinValue;
        foreach (var (k, v) in Levels)
        {
            if (!int.TryParse(k, out int l)) continue;
            if (l == level) return v;
            if (l < level && l > bestLvl) { best = v; bestLvl = l; }
        }
        return best ?? Levels.Where(x => int.TryParse(x.Key, out _)).OrderBy(x => int.Parse(x.Key)).Select(x => x.Value).FirstOrDefault();
    }

    public int MaxLevel => Levels.Keys.Select(k => int.TryParse(k, out var l) ? l : 0).DefaultIfEmpty(0).Max();
}

public sealed class GemLevel
{
    [JsonPropertyName("req")] public int Req { get; init; }
    /// <summary>Base critical strike chance, %.</summary>
    [JsonPropertyName("crit")] public double? Crit { get; init; }
    /// <summary>Added damage effectiveness (1 = 100%).</summary>
    [JsonPropertyName("eff")] public double? Effectiveness { get; init; }
    /// <summary>Attacks: base damage multiplier (1.5 = 150% of weapon damage).</summary>
    [JsonPropertyName("mult")] public double? BaseMultiplier { get; init; }
    /// <summary>Attacks: attack rate × (1 + this / 100).</summary>
    [JsonPropertyName("aspd")] public double? AttackSpeedMultiplier { get; init; }
    [JsonPropertyName("skill")] public Dictionary<string, double> Skill { get; init; } = [];
    [JsonPropertyName("mods")] public List<GemMod> Mods { get; init; } = [];
}

/// <summary>One PoB modifier: name, MORE/INC/BASE, value, ModFlags (all required), KeywordFlags (any), conditions.</summary>
public sealed class GemMod
{
    [JsonPropertyName("n")] public string Name { get; init; } = "";
    [JsonPropertyName("t")] public string Type { get; init; } = "";
    [JsonPropertyName("v")] public double Value { get; init; }
    [JsonPropertyName("f")] public List<string> Flags { get; init; } = [];
    [JsonPropertyName("k")] public List<string> Keywords { get; init; } = [];
    [JsonPropertyName("c")] public List<Dictionary<string, JsonElement>> Conditions { get; init; } = [];

    public GemMod Scaled(double factor) => new()
    {
        Name = Name, Type = Type, Value = Value * factor, Flags = Flags, Keywords = Keywords, Conditions = Conditions,
    };
}
