namespace PoeHelper.Core.Build;

/// <summary>
/// A stat the player tags in Settings → Upgrade tracker. Find upgrade ranks by resists first, then by the sum of the
/// tracked stats' changes, each divided by <see cref="Typical"/> (one good roll), so different units add up fairly.
/// </summary>
/// <param name="TradeId">Pseudo stat id to search on, if any.</param>
/// <param name="TradeText">Explicit stat text to search on, if any (resolved against the trade data).</param>
/// <param name="NativeRow">True when the compare table already has a row for it.</param>
public sealed record TrackedStat(string Key, string Label, double Typical, string Unit, Func<BuildStats, double> Value,
    string? TradeId = null, string? TradeText = null, bool NativeRow = false)
{
    public bool IsDamage => Key == TrackedStats.Damage;

    /// <summary>Change in the stat's own unit; for damage, the main skill's damage change in %.</summary>
    public double Change(BuildStats before, BuildStats after) =>
        IsDamage ? GearCompare.DamageChangePct(before, after) : Value(after) - Value(before);

    public string LabelFor(SkillProfile? skill) => IsDamage ? $"{skill?.Name ?? "Skill"} damage" : Label;
}

public static class TrackedStats
{
    public const string Damage = "damage";

    public static readonly IReadOnlyList<TrackedStat> All =
    [
        new(Damage, "Damage of the selected skill", 10, "%", _ => 0, NativeRow: true),
        new("life", "Maximum life", 70, "", s => s.Life, TradeId: "pseudo.pseudo_total_life", NativeRow: true),
        new("es", "Energy shield", 100, "", s => s.EnergyShield, TradeId: "pseudo.pseudo_total_energy_shield", NativeRow: true),
        new("mana", "Maximum mana (flat)", 60, "", s => s.Totals.Mana, TradeId: "pseudo.pseudo_total_mana"),
        new("regen", "Life regeneration /s", 30, "", s => s.LifeRegen, TradeId: "pseudo.pseudo_total_life_regen"),
        new("move", "Movement speed", 25, "%", s => s.MovementSpeed, TradeText: "#% increased Movement Speed", NativeRow: true),
        new("str", "Strength", 40, "", s => s.Str, TradeId: "pseudo.pseudo_total_strength", NativeRow: true),
        new("dex", "Dexterity", 40, "", s => s.Dex, TradeId: "pseudo.pseudo_total_dexterity", NativeRow: true),
        new("int", "Intelligence", 40, "", s => s.Int, TradeId: "pseudo.pseudo_total_intelligence", NativeRow: true),
        new("attackspeed", "Attack speed", 12, "%", s => s.Totals.AttackSpeedPct, TradeId: "pseudo.pseudo_total_attack_speed"),
        new("castspeed", "Cast speed", 15, "%", s => s.Totals.CastSpeedPct, TradeId: "pseudo.pseudo_total_cast_speed"),
        new("critchance", "Global crit chance", 30, "%", s => s.Totals.CritChancePct, TradeText: "#% increased Global Critical Strike Chance"),
        new("critmulti", "Global crit multiplier", 30, "%", s => s.Totals.CritMulti, TradeText: "+#% to Global Critical Strike Multiplier"),
        new("spelldamage", "Spell damage", 60, "%", s => s.Totals.SpellDamagePct, TradeText: "#% increased Spell Damage"),
        new("dotmulti", "Damage over time multiplier", 20, "%", s => s.Totals.DotMulti, TradeText: "+#% to Damage over Time Multiplier"),
        new("miniondamage", "Minion damage", 30, "%", s => s.Totals.MinionDamagePct, TradeText: "Minions deal #% increased Damage"),
        new("gemlevels", "+Levels to the selected skill", 1, "", s => s.SkillGemLevels, NativeRow: true),
    ];

    public static readonly string[] Default = ["life", Damage];

    /// <summary>The tracked stats for these keys, in catalogue order; the defaults when none are known.</summary>
    public static IReadOnlyList<TrackedStat> Resolve(IEnumerable<string>? keys)
    {
        var set = new HashSet<string>(keys ?? [], StringComparer.OrdinalIgnoreCase);
        var r = All.Where(s => set.Contains(s.Key)).ToList();
        return r.Count > 0 ? r : All.Where(s => Default.Contains(s.Key)).ToList();
    }
}
