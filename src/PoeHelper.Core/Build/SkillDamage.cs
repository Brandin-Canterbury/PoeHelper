using PoeHelper.Core.Data;
using PoeHelper.Core.Stats;

namespace PoeHelper.Core.Build;

/// <param name="Index">Relative damage per second (hit DPS + damage over time per second).</param>
/// <param name="Multi">DoT multiplier (DoT skills) or crit multiplier (hit skills), %.</param>
internal sealed record DamageResult(double Index, double Inc, double Multi, double Speed, double Crit, double Levels);

/// <summary>
/// The main skill's damage from Path of Building's gem data, the way PoB's CalcOffence works it out, reduced to
/// what gear compare needs:
///   hit  = Σ per damage type: (skill base or weapon × base multiplier + added × effectiveness) × (1 + inc) × more,
///          × attacks/casts per second × crit factor;
///   DoT  = Σ per damage type: base per second × (1 + inc) × more × (1 + DoT multiplier).
/// "More" comes from the gem itself and the supports linked to it; "increased" from gear, tree and supports.
/// Not modelled: conversion, ailments from hits, enemy resistances, conditional modifiers, minions (generic model).
/// </summary>
internal static class SkillDamage
{
    static readonly string[] Types = ["Physical", "Fire", "Cold", "Lightning", "Chaos"];
    static bool Elemental(string type) => type is "Fire" or "Cold" or "Lightning";

    public static DamageResult? Compute(CharacterBuild b, SkillProfile skill, StatTotals t, double life, double es, double extraLevels)
    {
        if (skill.IsMinion) return null;
        var gd = SkillData.Instance.Get(skill.Name);
        if (gd is null || gd.Support) return null;
        int level = Math.Max(1, skill.Gem.Level + (int)Math.Round(extraLevels));
        var L = gd.Level(level);
        if (L is null) return null;

        // Skill values (base damage etc.) and modifiers: the gem's own, then each linked support's.
        var values = new Dictionary<string, double>(gd.Skill);
        foreach (var (k, v) in L.Skill) values[k] = values.GetValueOrDefault(k) + v;
        var mods = new List<GemMod>(gd.Mods.Concat(L.Mods));
        if (skill.Gem.Quality > 0) mods.AddRange(gd.Quality.Select(q => q.Scaled(skill.Gem.Quality)));
        if (b.Items.TryGetValue(skill.Slot, out var home))
            foreach (var s in home.Gems.Where(g => g.Support && g.Group == skill.Gem.Group))
            {
                var sd = SkillData.Instance.Get(s.Name);
                if (sd is null) continue;
                mods.AddRange(sd.Mods);
                if (sd.Level(Math.Max(1, s.Level)) is { } sl) mods.AddRange(sl.Mods);
                if (s.Quality > 0) mods.AddRange(sd.Quality.Select(q => q.Scaled(s.Quality)));
            }

        // A skill-specific formula (only for this skill) can set its base values, e.g. Righteous Fire's life → damage.
        SkillFormulas.For(skill)?.PreDamage(values, life, es);

        bool attack = gd.HasFlag("attack");
        bool spell = gd.HasFlag("spell") && !attack;
        var hitFlags = new HashSet<string>(gd.Flags.Select(Pascal)) { "Hit" };
        var hitKeys = new HashSet<string> { "Hit", attack ? "Attack" : "Spell" };
        // PoB: a skill's own DoT keeps Spell/Area/Projectile/Attack only when the skill says so.
        var dotFlags = new HashSet<string> { "Dot" };
        foreach (var f in new[] { "Spell", "Area", "Projectile", "Attack" })
            if (hitFlags.Contains(f) && values.GetValueOrDefault($"dotIs{f}") > 0) dotFlags.Add(f);
        foreach (var f in hitFlags.Where(f => f is not ("Spell" or "Area" or "Projectile" or "Attack" or "Hit"))) dotFlags.Add(f);
        var dotKeys = new HashSet<string> { attack ? "Attack" : "Spell" };

        double Sum(string type, IReadOnlyCollection<string> names, ISet<string> flags, ISet<string> keys) =>
            mods.Where(m => m.Type == type && names.Contains(m.Name) && Applies(m, flags, keys, gd, skill.Name)).Sum(m => m.Value);
        double More(IReadOnlyCollection<string> names, ISet<string> flags, ISet<string> keys) =>
            mods.Where(m => m.Type == "MORE" && names.Contains(m.Name) && Applies(m, flags, keys, gd, skill.Name))
                .Aggregate(1.0, (acc, m) => acc * (1 + m.Value / 100));
        string[] DamageNames(string type) => Elemental(type) ? ["Damage", type + "Damage", "ElementalDamage"] : ["Damage", type + "Damage"];

        // Increased damage from gear/tree that applies to this damage type and these flags.
        double ItemInc(string type, ISet<string> flags, bool dot)
        {
            double inc = t.DamagePct + t.IncreasedFor(type) + (Elemental(type) ? t.ElementalDamagePct : 0);
            if (flags.Contains("Spell")) inc += t.SpellDamagePct;
            if (flags.Contains("Attack")) inc += t.AttackDamagePct + (Elemental(type) ? t.EleAttackDamagePct : 0);
            if (flags.Contains("Projectile")) inc += t.ProjectileDamagePct;
            if (flags.Contains("Area")) inc += t.AreaDamagePct;
            if (dot) inc += t.DamageOverTimePct + (type == "Fire" ? t.BurningDamagePct : 0);
            return inc;
        }

        // ---- hits
        double eff = L.Effectiveness ?? 1;
        double hit = 0, incShown = 0, speed = 0, crit = 0, critMulti = 0;
        var weapon = b.Items.GetValueOrDefault("Weapon");
        var weaponBase = attack ? WeaponBase(weapon) : null;
        // Spells without a hit (e.g. Righteous Fire) gain nothing from added flat damage.
        bool hasHit = attack || HasAnyHit(values);
        foreach (var type in hasHit ? Types : [])
        {
            double baseDmg;
            if (attack)
                baseDmg = (weaponBase?.GetValueOrDefault(type) ?? (type == "Physical" ? 5 : 0)) * (L.BaseMultiplier ?? 1);
            else
                baseDmg = (values.GetValueOrDefault(type + "Min") + values.GetValueOrDefault(type + "Max")) / 2;
            double added = t.AddedTo(attack, type)
                           + (Sum("BASE", [type + "Min"], hitFlags, hitKeys) + Sum("BASE", [type + "Max"], hitFlags, hitKeys)) / 2;
            double total = baseDmg + added * eff;
            if (total <= 0) continue;
            var names = DamageNames(type);
            double inc = ItemInc(type, hitFlags, false) + Sum("INC", names, hitFlags, hitKeys);
            hit += total * (1 + inc / 100) * More(names, hitFlags, hitKeys);
            incShown = Math.Max(incShown, inc);
        }
        // Weapon elemental damage whose element the API doesn't say: elemental increases only.
        if (attack && weaponBase?.GetValueOrDefault("Elemental") is > 0 and var eleBase)
        {
            string[] names = ["Damage", "ElementalDamage"];
            double inc = t.DamagePct + t.ElementalDamagePct + t.AttackDamagePct + t.EleAttackDamagePct
                         + (hitFlags.Contains("Area") ? t.AreaDamagePct : 0) + (hitFlags.Contains("Projectile") ? t.ProjectileDamagePct : 0)
                         + Sum("INC", names, hitFlags, hitKeys);
            hit += eleBase * (L.BaseMultiplier ?? 1) * (1 + inc / 100) * More(names, hitFlags, hitKeys);
        }
        if (hit > 0)
        {
            string[] speedNames = ["Speed"];
            if (attack)
            {
                double aps = weapon is not null && weapon.Props.TryGetValue("Attacks per Second", out var a)
                    ? Items.ItemParser.Numbers(a).DefaultIfEmpty(1.2).First() : 1.2;
                speed = aps * (1 + (L.AttackSpeedMultiplier ?? 0) / 100) * (1 + (t.AttackSpeedPct + Sum("INC", speedNames, hitFlags, hitKeys)) / 100);
            }
            else
            {
                double cast = gd.CastTime is > 0 ? gd.CastTime.Value : 1;
                speed = 1 / cast * (1 + (t.CastSpeedPct + Sum("INC", speedNames, hitFlags, hitKeys)) / 100);
            }
            speed *= More(speedNames, hitFlags, hitKeys);

            double baseCrit = attack
                ? (weapon is not null && weapon.Props.TryGetValue("Critical Strike Chance", out var c) ? Items.ItemParser.Numbers(c).DefaultIfEmpty(5).First() : 5)
                : L.Crit ?? 5;
            string[] critNames = ["CritChance"];
            crit = Math.Clamp(baseCrit * (1 + (t.CritChancePct + (spell ? t.SpellCritChancePct : 0) + Sum("INC", critNames, hitFlags, hitKeys)) / 100)
                              * More(critNames, hitFlags, hitKeys), 0, 100);
            critMulti = 150 + t.CritMulti + (spell ? t.SpellCritMulti : 0) + Sum("BASE", ["CritMultiplier"], hitFlags, hitKeys);
            hit *= speed * (1 + crit / 100 * Math.Max(0, critMulti - 100) / 100);
        }

        // ---- damage over time
        double dot = 0, dotMultiShown = 0;
        foreach (var type in Types)
        {
            double baseDot = values.GetValueOrDefault(type + "Dot");
            if (baseDot <= 0) continue;
            var keys = new HashSet<string>(dotKeys) { type + "Dot" };
            var names = DamageNames(type);
            double inc = ItemInc(type, dotFlags, true) + Sum("INC", names, dotFlags, keys);
            double multi = t.DotMulti + t.DotMultiFor(type)
                           + Sum("BASE", ["DotMultiplier", type + "DotMultiplier"], dotFlags, keys);
            dot += baseDot * (1 + inc / 100) * More(names, dotFlags, keys) * (1 + multi / 100);
            incShown = Math.Max(incShown, inc);
            dotMultiShown = Math.Max(dotMultiShown, multi);
        }

        if (hit <= 0 && dot <= 0) return null; // nothing the data can score (e.g. a buff): fall back
        bool dotSkill = dot > hit;
        return new DamageResult(hit + dot, incShown, dotSkill ? dotMultiShown : critMulti, dotSkill ? 1 : speed, dotSkill ? 0 : crit, extraLevels);
    }

    static bool HasAnyHit(Dictionary<string, double> v) => Types.Any(t => v.GetValueOrDefault(t + "Min") + v.GetValueOrDefault(t + "Max") > 0);

    /// <summary>PoB mod applies: every ModFlag present, any KeywordFlag present, SkillType/SkillName conditions met.</summary>
    static bool Applies(GemMod m, ISet<string> flags, ISet<string> keys, GemData gd, string skillName)
    {
        if (m.Flags.Any(f => !flags.Contains(f))) return false;
        if (m.Keywords.Count > 0 && !m.Keywords.Any(keys.Contains)) return false;
        foreach (var c in m.Conditions)
        {
            if (!c.TryGetValue("type", out var type)) continue;
            switch (type.GetString())
            {
                case "SkillType" when c.TryGetValue("skillType", out var st) && st.ValueKind == System.Text.Json.JsonValueKind.String:
                    if (!gd.HasType(st.GetString()!)) return false;
                    break;
                case "SkillName" when c.TryGetValue("skillName", out var sn) && sn.ValueKind == System.Text.Json.JsonValueKind.String:
                    if (!string.Equals(sn.GetString(), skillName, StringComparison.OrdinalIgnoreCase)) return false;
                    break;
            }
        }
        return true;
    }

    static string Pascal(string f) => f.Length == 0 ? f : char.ToUpperInvariant(f[0]) + f[1..];

    /// <summary>Weapon base damage per type from its property block (local mods already included).</summary>
    static Dictionary<string, double>? WeaponBase(EquippedItem? weapon)
    {
        if (weapon is null || !weapon.Props.ContainsKey("Attacks per Second")) return null;
        return new Dictionary<string, double>
        {
            ["Physical"] = AvgRange(weapon.Props.GetValueOrDefault("Physical Damage")),
            ["Chaos"] = AvgRange(weapon.Props.GetValueOrDefault("Chaos Damage")),
            ["Elemental"] = AvgRange(weapon.Props.GetValueOrDefault("Elemental Damage")),
        };
    }

    /// <summary>"50-76" → 63; "10-20, 5-60" → 47.5 (sum of averages).</summary>
    internal static double AvgRange(string? v)
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
}
