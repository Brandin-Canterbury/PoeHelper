using System.Text.RegularExpressions;
using PoeHelper.Core.Data;
using PoeHelper.Core.Items;

namespace PoeHelper.Core.Stats;

/// <summary>
/// The handful of stats the helper models: life, resists, attributes, ES/mana and key RF damage stats.
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
        foreach (var f in typeof(StatTotals).GetFields())
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
            case "Modifiers to Fire Resistance also apply to Cold and Lightning Resistances at #% of their Value":
                FireResToColdLightningPct += v; return true;
        }

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
