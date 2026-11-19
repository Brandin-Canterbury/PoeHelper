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
    /// <summary>Armour pieces' energy shield (their property block) + global flat, × global increases and Int.</summary>
    public double EnergyShield { get; init; }
    /// <summary>The DoT multiplier (DoT skills) or crit multiplier (hit skills) that applies to the main skill.</summary>
    public double DamageMulti { get; init; }
    public double IncreasedDamage { get; init; }
    /// <summary>
    /// Relative damage of the main skill: base × increased × (DoT multi or crit) × speed × gem levels.
    /// Only meaningful as a ratio between two gear sets.
    /// </summary>
    public double DamageIndex { get; init; }
    public SkillProfile? Skill { get; init; }
    public double Speed { get; init; }
    public double CritChance { get; init; }
    public double MovementSpeed { get; init; }
    /// <summary>Gem levels that reach the main skill (socketed + global).</summary>
    public double SkillGemLevels { get; init; }
    public StatTotals Totals { get; init; } = new();
}

/// <summary>
/// Character numbers from gear + tree + jewels + aura gems:
/// life = (38 + 12·level + flat + Str/2) × (1 + %inc); resists = mods − 60 (act penalty) vs 75 + max-res mods;
/// attributes vs the highest gem / item requirement; energy shield; the main skill's relative damage.
/// No conditional mods ("while on Low Life", flasks, charges) are counted.
/// </summary>
public static partial class BuildCalculator
{
    public const int ResistPenalty = -60;

    /// <summary>Weapon mods that only change the weapon's own numbers (already in its property block).</summary>
    static bool IsLocalWeaponMod(string line) => LocalWeaponRx().IsMatch(Data.TradeData.Skeleton(line));

    static bool IsWeapon(EquippedItem i) => i.Props.ContainsKey("Attacks per Second");

    /// <summary>On armour, "+# to maximum Energy Shield" is local: already in the piece's Energy Shield property.</summary>
    static bool IsArmourWithEs(EquippedItem i) => i.Props.ContainsKey("Energy Shield");

    static IEnumerable<string> GlobalMods(EquippedItem it) =>
        IsWeapon(it) ? it.Mods.Where(m => !IsLocalWeaponMod(m))
        : IsArmourWithEs(it) ? it.Mods.Where(m => Data.TradeData.Skeleton(m) != "+# to maximum Energy Shield")
        : it.Mods;

    public static BuildStats Compute(CharacterBuild b)
    {
        var lines = new List<string>(b.TreeStats);
        lines.AddRange(b.JewelMods);
        foreach (var it in b.Items.Values)
            lines.AddRange(GlobalMods(it));
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

        // Energy shield: 2% increased per 10 Intelligence.
        double esBase = t.EnergyShield + b.Items.Values.Where(IsArmourWithEs)
            .Sum(i => Items.ItemParser.Numbers(i.Props["Energy Shield"]).DefaultIfEmpty(0).First());
        double es = Math.Round(esBase * (1 + (t.EnergyShieldPct + Math.Floor(intl / 10) * 2) / 100));

        var skill = b.Skill;
        var dmg = Damage(b, skill, t, life);

        return new BuildStats
        {
            Life = life, Str = str, Dex = dex, Int = intl,
            Fire = fire, Cold = cold, Lightning = light, Chaos = chaos,
            StrNeed = Need(str, g => g.Str, i => i.ReqStr),
            DexNeed = Need(dex, g => g.Dex, i => i.ReqDex),
            IntNeed = Need(intl, g => g.Int, i => i.ReqInt),
            LifeRegen = regen, EnergyShield = es,
            DamageMulti = dmg.Multi, IncreasedDamage = dmg.Inc, DamageIndex = dmg.Index, Skill = skill,
            Speed = dmg.Speed, CritChance = dmg.Crit, SkillGemLevels = dmg.Levels,
            MovementSpeed = t.MovementSpeed,
            Totals = t,
        };
    }

    /// <summary>Damage per gem level (rough: most gems gain ~8-12% damage a level in the late teens).</summary>
    const double PerGemLevel = 0.10;

    /// <summary>
    /// Base damage assumed for a spell hit, from the gem's level, so flat "Adds # to # Damage to Spells" is weighed
    /// against something sensible. Real spell base damage varies a lot between gems; this is a middle-of-the-road guess.
    /// </summary>
    static double SpellBase(int level) => 10 + 9 * Math.Max(1, level);

    sealed record DamageResult(double Index, double Inc, double Multi, double Speed, double Crit, double Levels);

    static DamageResult Damage(CharacterBuild b, SkillProfile? skill, StatTotals t, double life)
    {
        if (skill is null)
            return new(1 + t.DamagePct / 100, t.DamagePct, 0, 1, 0, 0);

        // Gem levels that reach the skill: socketed ones on its own item, plus global "+1 to all X gems".
        double levels = 0;
        if (b.Items.TryGetValue(skill.Slot, out var home) && home.Gems.Any(g => g.Name == skill.Name))
            foreach (var m in home.Mods)
            {
                var sm = SocketedLevelRx().Match(Data.TradeData.Skeleton(m));
                if (!sm.Success) continue;
                var kind = sm.Groups[1].Value.Trim();
                if (kind.Length == 0 || KindMatches(skill, kind)) levels += Items.ItemParser.FirstInt(m);
            }
        foreach (var (kind, lv) in t.GlobalGemLevels)
            if (kind.Length == 0 || KindMatches(skill, kind)) levels += lv;
        double levelFactor = 1 + PerGemLevel * levels;

        var types = skill.Elements;
        bool ele = types.Any(e => e is "Fire" or "Cold" or "Lightning");

        if (skill.IsMinion)
        {
            double minc = t.MinionDamagePct;
            double mspeed = 1 + t.MinionSpeedPct / 100;
            return new((1 + minc / 100) * mspeed * levelFactor, minc, 0, mspeed, 0, levels);
        }

        // Increased damage that applies to this skill.
        double incd = t.DamagePct + types.Sum(t.IncreasedFor) + (ele ? t.ElementalDamagePct : 0);
        if (skill.IsSpell) incd += t.SpellDamagePct;
        if (skill.IsAttack) incd += t.AttackDamagePct + (ele ? t.EleAttackDamagePct : 0);
        if (skill.Has("Projectile")) incd += t.ProjectileDamagePct;
        if (skill.Has("AoE")) incd += t.AreaDamagePct;

        if (skill.IsDot)
        {
            incd += t.DamageOverTimePct + (types.Contains("Fire") ? t.BurningDamagePct : 0);
            double dm = t.DotMulti + types.Select(t.DotMultiFor).DefaultIfEmpty(0).Max();
            return new((1 + incd / 100) * (1 + dm / 100) * levelFactor, incd, dm, 1, 0, levels);
        }

        // Hits: base (weapon or spell) + flat added damage, × speed × crit.
        double baseDmg, speed, baseCrit;
        var weapon = b.Items.GetValueOrDefault("Weapon");
        if (skill.IsAttack && weapon is not null && IsWeapon(weapon))
        {
            baseDmg = AvgRange(weapon.Props.GetValueOrDefault("Physical Damage"))
                      + AvgRange(weapon.Props.GetValueOrDefault("Elemental Damage"))
                      + AvgRange(weapon.Props.GetValueOrDefault("Chaos Damage"));
            speed = Items.ItemParser.Numbers(weapon.Props.GetValueOrDefault("Attacks per Second") ?? "1").DefaultIfEmpty(1).First();
            baseCrit = Items.ItemParser.Numbers(weapon.Props.GetValueOrDefault("Critical Strike Chance") ?? "5").DefaultIfEmpty(5).First();
        }
        else if (skill.IsAttack)
        {
            (baseDmg, speed, baseCrit) = (5, 1.2, 5); // unarmed
        }
        else
        {
            (baseDmg, speed, baseCrit) = (SpellBase(skill.Gem.Level), 1, 6);
        }
        baseDmg += types.Sum(type => t.AddedTo(skill.IsAttack, type));
        double speedFactor = speed * (1 + (skill.IsAttack ? t.AttackSpeedPct : t.CastSpeedPct) / 100);
        double crit = Math.Min(100, baseCrit * (1 + (t.CritChancePct + (skill.IsSpell ? t.SpellCritChancePct : 0)) / 100));
        double critMulti = 50 + t.CritMulti + (skill.IsSpell ? t.SpellCritMulti : 0); // extra over a normal hit (base 150%)
        double critFactor = 1 + crit / 100 * critMulti / 100;
        return new(baseDmg * (1 + incd / 100) * speedFactor * critFactor * levelFactor, incd, critMulti + 100, speedFactor, crit, levels);
    }

    /// <summary>"Fire Spell" matches a fire spell; "Minion" a minion skill; "Spell" any spell; "Elemental" any element.</summary>
    static bool KindMatches(SkillProfile s, string kind) =>
        kind.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word => word switch
        {
            "Elemental" => s.Has("Fire") || s.Has("Cold") || s.Has("Lightning"),
            _ => s.Has(word),
        });

    /// <summary>"50-76" → 63; "10-20, 5-60" → 47.5 (sum of averages).</summary>
    static double AvgRange(string? v)
    {
        if (string.IsNullOrEmpty(v)) return 0;
        double total = 0;
        foreach (var part in v.Split(','))
        {
            var n = Items.ItemParser.Numbers(part.Replace('-', ' '));
            if (n.Count >= 2) total += (n[0] + n[1]) / 2;
            else if (n.Count == 1) total += n[0];
        }
        return total;
    }

    [GeneratedRegex(@"^(?:Adds # to # \w+ Damage|#% increased Physical Damage|#% increased Attack Speed|#% increased Critical Strike Chance|\+# to Accuracy Rating|#% increased Accuracy Rating)$")]
    private static partial Regex LocalWeaponRx();

    [GeneratedRegex(@"^\+# to Level of Socketed ((?:[A-Z][a-z]+ )*?)Gems$")]
    private static partial Regex SocketedLevelRx();

    [GeneratedRegex(@"^You and nearby [Aa]llies (?:gain |have )?(.+)$")]
    private static partial Regex AuraPrefix();
}
