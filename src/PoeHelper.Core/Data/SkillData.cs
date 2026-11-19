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
    [JsonPropertyName("constants")] public Dictionary<string, double> Constants { get; init; } = [];
    /// <summary>PoB's monsterAllyDamageTable: base minion damage by minion level (index 0 = level 1).</summary>
    [JsonPropertyName("allyDamage")] public List<double> AllyDamage { get; init; } = [];
    [JsonPropertyName("minions")] public Dictionary<string, MinionData> Minions { get; init; } = [];
    /// <summary>Skills minions use, by PoB id ("Melee", "SkeletonMinionProjectileCold").</summary>
    [JsonPropertyName("minionSkills")] public Dictionary<string, GemData> MinionSkills { get; init; } = [];

    /// <summary>PoB's skill effectiveness at an actor level (for minion skills, the minion's level).</summary>
    public double Effectiveness(GemData g, int actorLevel) =>
        (Constants.GetValueOrDefault("SkillDamageBaseEffectiveness", 3.885209)
         + Constants.GetValueOrDefault("SkillDamageIncrementalEffectiveness", 0.360246) * (actorLevel - 1))
        * (g.BaseEffectiveness ?? 1) * Math.Pow(1 + (g.IncrementalEffectiveness ?? 0), actorLevel - 1);

    public double AllyDamageAt(int level) =>
        AllyDamage.Count == 0 ? 0 : AllyDamage[Math.Clamp(level, 1, AllyDamage.Count) - 1];

    static readonly Lazy<SkillData> _instance = new(Load);
    public static SkillData Instance => _instance.Value;

    static SkillData Load()
    {
        using var s = typeof(SkillData).Assembly.GetManifestResourceStream("PoeHelper.Core.Data.skills.json")
                      ?? throw new InvalidOperationException("skills.json is not embedded");
        var d = JsonSerializer.Deserialize<SkillData>(s) ?? new SkillData();
        var byName = new Dictionary<string, GemData>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in d.Skills) byName.TryAdd(k, v); // a few PoB names differ only in case
        return new SkillData
        {
            Source = d.Source, Skills = byName, Constants = d.Constants, AllyDamage = d.AllyDamage,
            Minions = d.Minions, MinionSkills = d.MinionSkills,
        };
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
    /// <summary>Minion gems: the minions it summons (PoB ids); the first is the one scored.</summary>
    [JsonPropertyName("minions")] public List<string> Minions { get; init; } = [];
    [JsonPropertyName("baseEffectiveness")] public double? BaseEffectiveness { get; init; }
    [JsonPropertyName("incrementalEffectiveness")] public double? IncrementalEffectiveness { get; init; }

    /// <summary>The highest level whose requirement is at most <paramref name="actorLevel"/> (minion skills).</summary>
    public GemLevel? LevelForActor(int actorLevel) =>
        Levels.Values.Where(l => l.Req <= actorLevel).OrderByDescending(l => l.Req).FirstOrDefault()
        ?? Levels.Values.OrderBy(l => l.Req).FirstOrDefault();

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
    /// <summary>Minion skills: values to multiply by the effectiveness at the minion's level.</summary>
    [JsonPropertyName("skillEff")] public Dictionary<string, double> SkillEff { get; init; } = [];
    [JsonPropertyName("mods")] public List<GemMod> Mods { get; init; } = [];
}

/// <summary>A minion from PoB's Minions.lua.</summary>
public sealed class MinionData
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("skills")] public List<string> Skills { get; init; } = [];
    /// <summary>Base damage multiplier against the ally damage table.</summary>
    [JsonPropertyName("damage")] public double Damage { get; init; } = 1;
    [JsonPropertyName("damageSpread")] public double DamageSpread { get; init; }
    [JsonPropertyName("attackTime")] public double AttackTime { get; init; } = 1;
    [JsonPropertyName("limit")] public string? Limit { get; init; }
    [JsonPropertyName("ignoresAttackSpeed")] public bool IgnoresAttackSpeed { get; init; }
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
    /// <summary>A modifier for the skill's minions (PoB's MinionModifier), not for the player.</summary>
    [JsonPropertyName("m")] public bool Minion { get; init; }

    public GemMod Scaled(double factor) => new()
    {
        Name = Name, Type = Type, Value = Value * factor, Flags = Flags, Keywords = Keywords, Conditions = Conditions, Minion = Minion,
    };
}
