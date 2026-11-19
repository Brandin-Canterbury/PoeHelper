using System.Globalization;
using System.Text.RegularExpressions;
using PoeHelper.Core.Stats;

namespace PoeHelper.Core.Build;

/// <summary>
/// A skill with mechanics the generic tag model can't see (damage from life, self-burn). It only takes part in the
/// calculations when it's the skill being scored: the one picked in Settings → Skill to upgrade, or Auto's pick.
/// </summary>
public abstract class SkillFormula
{
    public abstract string Name { get; }
    /// <summary>One line for Settings and the compare panel.</summary>
    public abstract string Summary { get; }
    public abstract bool Matches(SkillProfile skill);

    /// <summary>
    /// Runs before the damage calculation (like PoB's preDamageFunc) and may set the skill's base values,
    /// e.g. "FireDot". Values come from Path of Building's gem data.
    /// </summary>
    public virtual void PreDamage(Dictionary<string, double> values, double life, double energyShield) { }

    /// <summary>Life lost per second to the skill itself (0 if none).</summary>
    public virtual double SelfDamagePerSecond(SkillProfile skill, double life, Resist fire) => 0;
}

public static class SkillFormulas
{
    public static readonly IReadOnlyList<SkillFormula> All = [new RighteousFireFormula()];

    public static SkillFormula? For(SkillProfile? skill) => skill is null ? null : All.FirstOrDefault(f => f.Matches(skill));

    public static SkillFormula? ForName(string? gemName) =>
        string.IsNullOrWhiteSpace(gemName) ? null : All.FirstOrDefault(f => gemName.Trim().Equals(f.Name, StringComparison.OrdinalIgnoreCase));

    internal static double Percent(IEnumerable<string> mods, Regex rx, double fallback)
    {
        foreach (var m in mods)
        {
            var x = rx.Match(m);
            if (x.Success) return double.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        return fallback;
    }
}

/// <summary>
/// Righteous Fire: deals a % of maximum life and energy shield as base fire damage per second and burns you for a %
/// of maximum life, reduced by fire resistance. So life is damage, and fire res and regen are sustain.
/// </summary>
public sealed partial class RighteousFireFormula : SkillFormula
{
    public override string Name => "Righteous Fire";
    public override string Summary =>
        "Righteous Fire formula on: base burn = 70% of life + 70% of energy shield per second (Path of Building's), and RF's own burn is checked against your regen.";
    public override bool Matches(SkillProfile skill) => skill.Name.Equals(Name, StringComparison.OrdinalIgnoreCase); // the variants have their own PoB code

    /// <summary>PoB's preDamageFunc: FireDot = life × RFLifeMultiplier + energy shield × RFESMultiplier.</summary>
    public override void PreDamage(Dictionary<string, double> values, double life, double energyShield) =>
        values["FireDot"] = life * values.GetValueOrDefault("RFLifeMultiplier", 0.7) + energyShield * values.GetValueOrDefault("RFESMultiplier", 0.7);

    public override double SelfDamagePerSecond(SkillProfile skill, double life, Resist fire) =>
        life * SkillFormulas.Percent(skill.Gem.Mods, BurnRx(), 90) / 100 * (1 - fire.Effective / 100);

    [GeneratedRegex(@"You Burn for ([\d.]+)% of your Maximum Life per second")]
    private static partial Regex BurnRx();
}
