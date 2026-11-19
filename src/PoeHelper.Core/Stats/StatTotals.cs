using System.Text.RegularExpressions;
using PoeHelper.Core.Data;
using PoeHelper.Core.Items;

namespace PoeHelper.Core.Stats;

/// <summary>
/// The handful of stats the helper models: life, resists, attributes, ES/mana and the damage stats the main-skill model uses.
/// Summed from mod text, so it works the same for clipboard items and character-API items.
/// </summary>
public sealed partial class StatTotals
{
    public double Life, LifePct, Mana, EnergyShield, EnergyShieldPct;
    public double Fire, Cold, Lightning, Chaos;
    public double MaxFire, MaxCold, MaxLightning, MaxChaos;
    public double Str, Dex, Int;
    public double FireDotMulti, DotMulti, FireDamagePct, BurningDamagePct, SpellDamagePct, ElementalDamagePct, DamagePct;
    public double LifeRegenPct, LifeRegenFlat, LifeRegenRate, MovementSpeed;
    public double GemLevelsFire, GemLevelsAll;
    public double StrPct, DexPct, IntPct, AttributesPct;
    public double DamageOverTimePct, AreaDamagePct;
    // Generic damage (any skill): per-type increases, speed, crit, flat added damage, minions.
    public double ColdDamagePct, LightningDamagePct, PhysicalDamagePct, ChaosDamagePct;
    public double AttackDamagePct, ProjectileDamagePct, EleAttackDamagePct, MinionDamagePct;
    public double AttackSpeedPct, CastSpeedPct, MinionSpeedPct;
    public double CritChancePct, SpellCritChancePct, CritMulti, SpellCritMulti;
    public double ColdDotMulti, ChaosDotMulti, PhysDotMulti;
    /// <summary>Average flat damage added to attacks / spells, per type.</summary>
    public double AtkFire, AtkCold, AtkLightning, AtkPhysical, AtkChaos;
    public double SpellFire, SpellCold, SpellLightning, SpellPhysical, SpellChaos;
    /// <summary>"+1 to Level of all Fire Spell Skill Gems" → ("Fire Spell", 1); "+1 to Level of all Skill Gems" → ("", 1).</summary>
    public List<(string Kind, double Levels)> GlobalGemLevels = [];

    public double AddedTo(bool attack, string type) => (attack, type) switch
    {
        (true, "Fire") => AtkFire, (true, "Cold") => AtkCold, (true, "Lightning") => AtkLightning,
        (true, "Physical") => AtkPhysical, (true, "Chaos") => AtkChaos,
        (false, "Fire") => SpellFire, (false, "Cold") => SpellCold, (false, "Lightning") => SpellLightning,
        (false, "Physical") => SpellPhysical, (false, "Chaos") => SpellChaos,
        _ => 0,
    };

    public double IncreasedFor(string type) => type switch
    {
        "Fire" => FireDamagePct, "Cold" => ColdDamagePct, "Lightning" => LightningDamagePct,
        "Physical" => PhysicalDamagePct, "Chaos" => ChaosDamagePct, _ => 0,
    };

    public double DotMultiFor(string type) => type switch
    {
        "Fire" => FireDotMulti, "Cold" => ColdDotMulti, "Chaos" => ChaosDotMulti, "Physical" => PhysDotMulti, _ => 0,
    };
    /// <summary>e.g. 50 from "Modifiers to Fire Resistance also apply to Cold and Lightning Resistances at 50% of their Value".</summary>
    public double FireResToColdLightningPct;

    public double TotalElementalRes => Fire + Cold + Lightning;

    public static StatTotals FromMods(IEnumerable<ItemMod> mods)
    {
        var t = new StatTotals();
        foreach (var m in mods) t.Add(m.Text, m.Values);
        return t;
    }

    public static StatTotals FromTexts(IEnumerable<string> lines)
    {
        var t = new StatTotals();
        foreach (var l in lines) t.Add(l, ItemParser.Numbers(l));
        return t;
    }

    public StatTotals Plus(StatTotals o, double sign = 1)
    {
        var r = new StatTotals();
        foreach (var f in typeof(StatTotals).GetFields().Where(f => f.FieldType == typeof(double)))
            f.SetValue(r, (double)f.GetValue(this)! + sign * (double)f.GetValue(o)!);
        return r;
    }

    /// <summary>Adds one mod line. Returns false when the line is not one we model.</summary>
    public bool Add(string text, IReadOnlyList<double> values)
    {
        if (values.Count == 0) return false;
        double v = values[0];
        var sk = TradeData.Skeleton(text);
        if (sk.StartsWith("-#")) sk = "+#" + sk[2..]; // value already negative
        bool reduced = sk.Contains(" reduced ");
        if (reduced) { sk = sk.Replace(" reduced ", " increased "); v = -v; }

        switch (sk)
        {
            case "+# to maximum Life": Life += v; return true;
            case "#% increased maximum Life": LifePct += v; return true;
            case "+# to maximum Mana": Mana += v; return true;
            case "+# to maximum Energy Shield": EnergyShield += v; return true;
            case "#% increased maximum Energy Shield": EnergyShieldPct += v; return true;
            case "+#% to Fire Damage over Time Multiplier": FireDotMulti += v; return true;
            case "+#% to Damage over Time Multiplier": DotMulti += v; return true;
            case "#% increased Fire Damage": FireDamagePct += v; return true;
            case "#% increased Burning Damage": BurningDamagePct += v; return true;
            case "#% increased Spell Damage": SpellDamagePct += v; return true;
            case "#% increased Elemental Damage": ElementalDamagePct += v; return true;
            case "#% increased Damage": DamagePct += v; return true;
            case "Regenerate #% of Life per second": LifeRegenPct += v; return true;
            case "Regenerate # Life per second": LifeRegenFlat += v; return true;
            case "Lose # Life per second": LifeRegenFlat -= v; return true;
            case "#% increased Movement Speed": MovementSpeed += v; return true;
            case "+# to Level of Socketed Fire Gems": GemLevelsFire += v; return true;
            case "+# to Level of Socketed Gems": GemLevelsAll += v; return true;
            case "#% increased Life Regeneration rate": LifeRegenRate += v; return true;
            case "#% increased Strength": StrPct += v; return true;
            case "#% increased Dexterity": DexPct += v; return true;
            case "#% increased Intelligence": IntPct += v; return true;
            case "#% increased Attributes": AttributesPct += v; return true;
            case "#% increased Damage over Time": DamageOverTimePct += v; return true;
            case "#% increased Area Damage": AreaDamagePct += v; return true;
            case "+#% to all maximum Elemental Resistances": MaxFire += v; MaxCold += v; MaxLightning += v; return true;
            case "+#% to all maximum Resistances": MaxFire += v; MaxCold += v; MaxLightning += v; MaxChaos += v; return true;
            case "#% increased Cold Damage": ColdDamagePct += v; return true;
            case "#% increased Lightning Damage": LightningDamagePct += v; return true;
            case "#% increased Global Physical Damage":
            case "#% increased Physical Damage": PhysicalDamagePct += v; return true;
            case "#% increased Chaos Damage": ChaosDamagePct += v; return true;
            case "#% increased Attack Damage": AttackDamagePct += v; return true;
            case "#% increased Projectile Damage": ProjectileDamagePct += v; return true;
            case "#% increased Elemental Damage with Attack Skills": EleAttackDamagePct += v; return true;
            case "Minions deal #% increased Damage": MinionDamagePct += v; return true;
            case "#% increased Attack Speed": AttackSpeedPct += v; return true;
            case "#% increased Cast Speed": CastSpeedPct += v; return true;
            case "#% increased Attack and Cast Speed": AttackSpeedPct += v; CastSpeedPct += v; return true;
            case "Minions have #% increased Attack Speed":
            case "Minions have #% increased Cast Speed":
            case "Minions have #% increased Attack and Cast Speed": MinionSpeedPct += v; return true;
            case "#% increased Global Critical Strike Chance":
            case "#% increased Critical Strike Chance": CritChancePct += v; return true;
            case "#% increased Critical Strike Chance for Spells":
            case "#% increased Spell Critical Strike Chance": SpellCritChancePct += v; return true;
            case "+#% to Global Critical Strike Multiplier": CritMulti += v; return true;
            case "+#% to Critical Strike Multiplier for Spells": SpellCritMulti += v; return true;
            case "+#% to Cold Damage over Time Multiplier": ColdDotMulti += v; return true;
            case "+#% to Chaos Damage over Time Multiplier": ChaosDotMulti += v; return true;
            case "+#% to Physical Damage over Time Multiplier": PhysDotMulti += v; return true;
            case "Modifiers to Fire Resistance also apply to Cold and Lightning Resistances at #% of their Value":
                FireResToColdLightningPct += v; return true;
        }

        var add = AddsRx().Match(sk);
        if (add.Success && values.Count >= 2)
        {
            double avg = (values[0] + values[1]) / 2;
            string type = add.Groups[1].Value, to = add.Groups[2].Value;
            bool atk = to is "" or " to Attacks" or " to Spells and Attacks", spell = to is " to Spells" or " to Spells and Attacks";
            if (atk) AddFlat(true, type, avg);
            if (spell) AddFlat(false, type, avg);
            return true;
        }
        var lvl = GemLevelRx().Match(sk);
        if (lvl.Success) { GlobalGemLevels.Add((lvl.Groups[1].Value.Trim(), v)); return true; }

        var m = ResRx().Match(sk);
        if (m.Success)
        {
            bool max = m.Groups[1].Success;
            foreach (var e in Elements(m.Groups[2].Value)) AddRes(e, v, max);
            return true;
        }
        m = AttrRx().Match(sk);
        if (m.Success)
        {
            var what = m.Groups[1].Value;
            if (what == "all Attributes") { Str += v; Dex += v; Int += v; return true; }
            bool any = false;
            foreach (var part in what.Split(" and "))
            {
                switch (part)
                {
                    case "Strength": Str += v; any = true; break;
                    case "Dexterity": Dex += v; any = true; break;
                    case "Intelligence": Int += v; any = true; break;
                }
            }
            return any;
        }
        return false;
    }

    void AddFlat(bool attack, string type, double avg)
    {
        switch (type)
        {
            case "Fire": if (attack) AtkFire += avg; else SpellFire += avg; break;
            case "Cold": if (attack) AtkCold += avg; else SpellCold += avg; break;
            case "Lightning": if (attack) AtkLightning += avg; else SpellLightning += avg; break;
            case "Physical": if (attack) AtkPhysical += avg; else SpellPhysical += avg; break;
            case "Chaos": if (attack) AtkChaos += avg; else SpellChaos += avg; break;
        }
    }

    [GeneratedRegex(@"^Adds # to # (Fire|Cold|Lightning|Physical|Chaos) Damage( to Attacks| to Spells| to Spells and Attacks)?$")]
    private static partial Regex AddsRx();

    [GeneratedRegex(@"^\+# to Level of all ((?:[A-Z][a-z]+ )*?)(?:Skill )?Gems$")]
    private static partial Regex GemLevelRx();

    static IEnumerable<string> Elements(string s)
    {
        if (s is "all Elemental") return ["Fire", "Cold", "Lightning"];
        if (s is "all") return ["Fire", "Cold", "Lightning", "Chaos"];
        return s.Split(" and ");
    }

    void AddRes(string element, double v, bool max)
    {
        switch (element)
        {
            case "Fire": if (max) MaxFire += v; else Fire += v; break;
            case "Cold": if (max) MaxCold += v; else Cold += v; break;
            case "Lightning": if (max) MaxLightning += v; else Lightning += v; break;
            case "Chaos": if (max) MaxChaos += v; else Chaos += v; break;
        }
    }

    [GeneratedRegex(@"^\+#% to (maximum )?(all Elemental|all|(?:Fire|Cold|Lightning|Chaos)(?: and (?:Fire|Cold|Lightning|Chaos))*) Resistances?$")]
    private static partial Regex ResRx();

    [GeneratedRegex(@"^\+# to (all Attributes|(?:Strength|Dexterity|Intelligence)(?: and (?:Strength|Dexterity|Intelligence))?)$")]
    private static partial Regex AttrRx();
}
