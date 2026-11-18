using System.Text.RegularExpressions;
using PoeHelper.Core.Items;
using PoeHelper.Core.Stats;

namespace PoeHelper.Core.Build;

public sealed record Resist(double Total, double Max)
{
    public double Effective => Math.Min(Total, Max);
    public double Overcap => Total - Max;
    public bool Capped => Total >= Max;
}

public sealed record AttributeNeed(double Have, int Need, string NeededBy)
{
    public bool Met => Have >= Need;
}

/// <summary>A light, PoB-style snapshot of the numbers gear compare cares about.</summary>
public sealed class BuildStats
{
    public double Life { get; init; }
    public double Str { get; init; }
    public double Dex { get; init; }
    public double Int { get; init; }
    public Resist Fire { get; init; } = new(0, 75);
    public Resist Cold { get; init; } = new(0, 75);
    public Resist Lightning { get; init; } = new(0, 75);
    public Resist Chaos { get; init; } = new(0, 75);
    public AttributeNeed StrNeed { get; init; } = new(0, 0, "");
    public AttributeNeed DexNeed { get; init; } = new(0, 0, "");
    public AttributeNeed IntNeed { get; init; } = new(0, 0, "");
    public double LifeRegen { get; init; }
    /// <summary>Righteous Fire self-burn after fire resistance (0 without RF).</summary>
    public double RfDegen { get; init; }
    public bool HasRf { get; init; }
    public double NetRegen => LifeRegen - RfDegen;
    public double FireDotMulti { get; init; }
    public double IncreasedDamage { get; init; }
    /// <summary>Relative RF damage: base (% of life) × increased × DoT multi. Only meaningful as a ratio.</summary>
    public double RfDamageIndex { get; init; }
    public double MovementSpeed { get; init; }
    public double GemLevelsFire { get; init; }
    public StatTotals Totals { get; init; } = new();
}

/// <summary>
/// Character numbers from gear + tree + jewels + aura gems:
/// life = (38 + 12·level + flat + Str/2) × (1 + %inc); resists = mods − 60 (act penalty) vs 75 + max-res mods;
/// attributes vs the highest gem / item requirement; RF sustain = regen − RF burn × (1 − fire res).
/// No conditional mods ("while on Low Life", flasks, charges) are counted.
/// </summary>
public static partial class BuildCalculator
{
    public const int ResistPenalty = -60;

    public static BuildStats Compute(CharacterBuild b)
    {
        var lines = new List<string>(b.TreeStats);
        lines.AddRange(b.JewelMods);
        foreach (var it in b.Items.Values) lines.AddRange(it.Mods);
        var gems = b.Items.Values.SelectMany(i => i.Gems).ToList();
        // Aura gems: "You and nearby allies gain +34% to all Elemental Resistances".
        foreach (var g in gems.Where(g => !g.Support))
            foreach (var m in g.Mods)
            {
                var a = AuraPrefix().Match(m);
                if (a.Success) lines.Add(a.Groups[1].Value);
            }
        var t = StatTotals.FromTexts(lines);

        double str = Math.Floor((b.BaseStr + t.Str) * (1 + (t.StrPct + t.AttributesPct) / 100));
        double dex = Math.Floor((b.BaseDex + t.Dex) * (1 + (t.DexPct + t.AttributesPct) / 100));
        double intl = Math.Floor((b.BaseInt + t.Int) * (1 + (t.IntPct + t.AttributesPct) / 100));

        double baseLife = 38 + 12 * b.Level + t.Life + Math.Floor(str / 2);
        double life = Math.Round(baseLife * (1 + t.LifePct / 100));

        double conv = t.FireResToColdLightningPct / 100;
        Resist R(double mods, double maxMods) => new(mods + ResistPenalty, Math.Min(90, 75 + maxMods));
        var fire = R(t.Fire, t.MaxFire);
        var cold = R(t.Cold + t.Fire * conv, t.MaxCold);
        var light = R(t.Lightning + t.Fire * conv, t.MaxLightning);
        var chaos = R(t.Chaos, t.MaxChaos);

        AttributeNeed Need(double have, Func<GemInfo, int> gemReq, Func<EquippedItem, int> itemReq)
        {
            int need = 0; string by = "";
            foreach (var g in gems) if (gemReq(g) > need) { need = gemReq(g); by = $"{g.Name} (level {g.Level})"; }
            foreach (var i in b.Items.Values) if (itemReq(i) > need) { need = itemReq(i); by = i.Name; }
            return new AttributeNeed(have, need, by);
        }

        double regen = (life * t.LifeRegenPct / 100 + t.LifeRegenFlat) * (1 + t.LifeRegenRate / 100);

        var rf = gems.FirstOrDefault(g => g.Name.Contains("Righteous Fire"));
        double burnPct = 0, dmgPct = 0;
        if (rf is not null)
            foreach (var m in rf.Mods)
            {
                var burn = RfBurn().Match(m);
                if (burn.Success) burnPct = double.Parse(burn.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                var dmg = RfDamage().Match(m);
                if (dmg.Success) dmgPct = double.Parse(dmg.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
        double degen = life * burnPct / 100 * (1 - fire.Effective / 100);

        // Everything that scales RF: it's a fire, burning, spell, elemental, area damage-over-time skill.
        double inc = t.FireDamagePct + t.BurningDamagePct + t.SpellDamagePct + t.ElementalDamagePct + t.DamagePct
                     + t.DamageOverTimePct + t.AreaDamagePct;
        double multi = t.FireDotMulti + t.DotMulti;
        double index = (dmgPct > 0 ? dmgPct : 70) / 100 * life * (1 + inc / 100) * (1 + multi / 100);

        return new BuildStats
        {
            Life = life, Str = str, Dex = dex, Int = intl,
            Fire = fire, Cold = cold, Lightning = light, Chaos = chaos,
            StrNeed = Need(str, g => g.Str, i => i.ReqStr),
            DexNeed = Need(dex, g => g.Dex, i => i.ReqDex),
            IntNeed = Need(intl, g => g.Int, i => i.ReqInt),
            LifeRegen = regen, RfDegen = degen, HasRf = rf is not null,
            FireDotMulti = multi, IncreasedDamage = inc, RfDamageIndex = index,
            MovementSpeed = t.MovementSpeed, GemLevelsFire = t.GemLevelsFire + t.GemLevelsAll,
            Totals = t,
        };
    }

    [GeneratedRegex(@"^You and nearby [Aa]llies (?:gain |have )?(.+)$")]
    private static partial Regex AuraPrefix();

    [GeneratedRegex(@"You Burn for ([\d.]+)% of your Maximum Life per second")]
    private static partial Regex RfBurn();

    [GeneratedRegex(@"Deals ([\d.]+)% of your Maximum Life as Base Fire Damage per second")]
    private static partial Regex RfDamage();
}
