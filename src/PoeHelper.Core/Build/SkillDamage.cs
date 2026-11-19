using PoeHelper.Core.Data;
using PoeHelper.Core.Stats;

namespace PoeHelper.Core.Build;

/// <param name="Index">Relative damage per second (hit DPS + damage over time per second; minions: × how many).</param>
/// <param name="Multi">DoT multiplier (DoT skills) or crit multiplier (hit skills), %.</param>
internal sealed record DamageResult(double Index, double Inc, double Multi, double Speed, double Crit, double Levels, int Count = 1);

/// <summary>
/// The main skill's damage from Path of Building's gem data, the way PoB's CalcOffence works it out, reduced to
/// what gear compare needs:
///   hit  = Σ per damage type: (skill base or weapon × base multiplier + added × effectiveness) × (1 + inc) × more,
///          × attacks/casts per second × crit factor;
///   DoT  = Σ per damage type: base per second × (1 + inc) × more × (1 + DoT multiplier).
/// Minion gems run the same maths for the minion (PoB's CalcActiveSkill): its level from the gem, its "weapon" from the
/// ally damage table, its own skill, the linked supports and every "Minions …" modifier, × the number of minions.
/// Not modelled: conversion, ailments from hits, enemy resistances, conditional modifiers, spectres.
/// </summary>
internal static class SkillDamage
{
    static readonly string[] Types = ["Physical", "Fire", "Cold", "Lightning", "Chaos"];
    static bool Elemental(string type) => type is "Fire" or "Cold" or "Lightning" or "Elemental";

    /// <summary>Who deals the damage: the player (gear, tree) or a minion (only "Minions …" stats reach it).</summary>
    sealed class Actor
    {
        public required Func<string, ISet<string>, bool, double> ItemInc { get; init; }
        public required double AttackSpeedInc { get; init; }
        public required double CastSpeedInc { get; init; }
        public required Func<bool, double> CritInc { get; init; }
        public required Func<bool, double> CritMulti { get; init; }
        public required Func<bool, string, double> Added { get; init; }
        public required Func<string, double> DotMulti { get; init; }
        /// <summary>Base damage per type for attacks, attacks per second, base crit.</summary>
        public (Dictionary<string, double> Base, double Aps, double Crit)? Weapon { get; init; }
    }

    public static DamageResult? Compute(CharacterBuild b, SkillProfile skill, StatTotals t, double life, double es, double extraLevels)
    {
        var data = SkillData.Instance;
        var gd = data.Get(skill.Name);
        if (gd is null || gd.Support) return null;
        int level = Math.Max(1, skill.Gem.Level + (int)Math.Round(extraLevels));
        var L = gd.Level(level);
        if (L is null) return null;

        // The gem's and its linked supports' modifiers (supports at their own level and quality).
        var gemMods = new List<GemMod>(gd.Mods.Concat(L.Mods));
        if (skill.Gem.Quality > 0) gemMods.AddRange(gd.Quality.Select(q => q.Scaled(skill.Gem.Quality)));
        if (b.Items.TryGetValue(skill.Slot, out var home))
            foreach (var s in home.Gems.Where(g => g.Support && g.Group == skill.Gem.Group))
            {
                var sd = data.Get(s.Name);
                if (sd is null) continue;
                gemMods.AddRange(sd.Mods);
                if (sd.Level(Math.Max(1, s.Level)) is { } sl) gemMods.AddRange(sl.Mods);
                if (s.Quality > 0) gemMods.AddRange(sd.Quality.Select(q => q.Scaled(s.Quality)));
            }

        if (gd.Minions.Count > 0) return Minion(gd, L, gemMods, t, extraLevels);
        if (skill.IsMinion) return null; // e.g. Raise Spectre: which spectre isn't known

        var values = new Dictionary<string, double>(gd.Skill);
        foreach (var (k, v) in L.Skill) values[k] = values.GetValueOrDefault(k) + v;
        // A skill-specific formula (only for this skill) can set its base values, e.g. Righteous Fire's life → damage.
        SkillFormulas.For(skill)?.PreDamage(values, life, es);

        var weapon = b.Items.GetValueOrDefault("Weapon");
        var player = new Actor
        {
            ItemInc = (type, flags, dot) =>
            {
                double inc = t.DamagePct + t.IncreasedFor(type) + (Elemental(type) ? t.ElementalDamagePct : 0);
                if (flags.Contains("Spell")) inc += t.SpellDamagePct;
                if (flags.Contains("Attack")) inc += t.AttackDamagePct + (Elemental(type) ? t.EleAttackDamagePct : 0);
                if (flags.Contains("Projectile")) inc += t.ProjectileDamagePct;
                if (flags.Contains("Area")) inc += t.AreaDamagePct;
                if (dot) inc += t.DamageOverTimePct + (type == "Fire" ? t.BurningDamagePct : 0);
                return inc;
            },
            AttackSpeedInc = t.AttackSpeedPct,
            CastSpeedInc = t.CastSpeedPct,
            CritInc = spell => t.CritChancePct + (spell ? t.SpellCritChancePct : 0),
            CritMulti = spell => 150 + t.CritMulti + (spell ? t.SpellCritMulti : 0),
            Added = (attack, type) => t.AddedTo(attack, type),
            DotMulti = type => t.DotMulti + t.DotMultiFor(type),
            Weapon = WeaponOf(weapon),
        };
        var r = Engine(gd, L, values, gemMods.Where(m => !m.Minion).ToList(), player, skill.Name);
        return r is null ? null : r with { Levels = extraLevels };
    }

    /// <summary>PoB CalcActiveSkill for minions: level, weapon from the ally damage table, first skill, × count.</summary>
    static DamageResult? Minion(GemData gem, GemLevel gemLevel, List<GemMod> gemMods, StatTotals t, double extraLevels)
    {
        var data = SkillData.Instance;
        var minion = gem.Minions.Select(id => data.Minions.GetValueOrDefault(id)).FirstOrDefault(m => m is not null);
        if (minion is null) return null;
        int mLevel = Math.Clamp((int)(gemLevel.Skill.GetValueOrDefault("minionLevel") is > 0 and var ml ? ml : gemLevel.Req), 1, 100);
        var msd = minion.Skills.Select(id => data.MinionSkills.GetValueOrDefault(id)).FirstOrDefault(s => s is not null);
        if (msd is null) return null;
        var ml1 = msd.LevelForActor(mLevel);
        if (ml1 is null) return null;

        var values = new Dictionary<string, double>(msd.Skill);
        foreach (var (k, v) in ml1.Skill) values[k] = values.GetValueOrDefault(k) + v;
        double eff = data.Effectiveness(msd, mLevel);
        foreach (var (k, v) in ml1.SkillEff) values[k] = values.GetValueOrDefault(k) + Math.Round(v * eff);

        // What reaches the minion: its skill's and its own mods, the supports (they support minion skills too),
        // and every "Minions …" modifier from the gem and supports.
        var mods = new List<GemMod>(msd.Mods.Concat(ml1.Mods).Concat(minion.Mods));
        foreach (var m in gemMods)
        {
            if (m.Minion) mods.Add(new GemMod { Name = m.Name, Type = m.Type, Value = m.Value, Flags = m.Flags, Keywords = m.Keywords, Conditions = m.Conditions });
            else if (!m.Name.EndsWith("Limit")) mods.Add(m);
        }

        double baseDmg = data.AllyDamageAt(mLevel) * minion.Damage * (minion.IgnoresAttackSpeed ? 1 : minion.AttackTime);
        var actor = new Actor
        {
            ItemInc = (_, _, _) => t.MinionDamagePct,
            AttackSpeedInc = t.MinionSpeedPct,
            CastSpeedInc = t.MinionSpeedPct,
            CritInc = _ => 0,
            CritMulti = _ => 150,
            Added = (_, _) => 0,
            DotMulti = _ => 0,
            Weapon = (new Dictionary<string, double> { ["Physical"] = baseDmg }, 1 / Math.Max(0.05, minion.AttackTime), 5),
        };
        var r = Engine(msd, ml1, values, mods, actor, msd.Id, minionSkill: true);
        if (r is null) return null;
        int count = minion.Limit is { } lim
            ? Math.Max(1, (int)Math.Round(gemMods.Where(m => m.Name == lim && m.Type == "BASE").Sum(m => m.Value)))
            : 1;
        return r with { Index = r.Index * count, Levels = extraLevels, Count = count };
    }

    static DamageResult? Engine(GemData gd, GemLevel L, Dictionary<string, double> values, List<GemMod> mods, Actor actor,
        string skillName, bool minionSkill = false)
    {
        bool attack = gd.HasFlag("attack");
        bool spell = gd.HasFlag("spell") && !attack;
        var hitFlags = new HashSet<string>(gd.Flags.Select(Pascal)) { "Hit" };
        if (minionSkill && hitFlags.Contains("Melee")) hitFlags.Remove("Projectile"); // a minion's default attack is melee
        var hitKeys = new HashSet<string> { "Hit", attack ? "Attack" : "Spell" };
        // PoB: a skill's own DoT keeps Spell/Area/Projectile/Attack only when the skill says so.
        var dotFlags = new HashSet<string> { "Dot" };
        foreach (var f in new[] { "Spell", "Area", "Projectile", "Attack" })
            if (hitFlags.Contains(f) && values.GetValueOrDefault($"dotIs{f}") > 0) dotFlags.Add(f);
        foreach (var f in hitFlags.Where(f => f is not ("Spell" or "Area" or "Projectile" or "Attack" or "Hit"))) dotFlags.Add(f);
        var dotKeys = new HashSet<string> { attack ? "Attack" : "Spell" };

        double Sum(string type, IReadOnlyCollection<string> names, ISet<string> flags, ISet<string> keys) =>
            mods.Where(m => m.Type == type && names.Contains(m.Name) && Applies(m, flags, keys, gd, skillName)).Sum(m => m.Value);
        double More(IReadOnlyCollection<string> names, ISet<string> flags, ISet<string> keys) =>
            mods.Where(m => m.Type == "MORE" && names.Contains(m.Name) && Applies(m, flags, keys, gd, skillName))
                .Aggregate(1.0, (acc, m) => acc * (1 + m.Value / 100));
        string[] DamageNames(string type) => Elemental(type) ? ["Damage", type + "Damage", "ElementalDamage"] : ["Damage", type + "Damage"];

        // ---- hits
        double eff = L.Effectiveness ?? 1;
        double hit = 0, incShown = 0, speed = 0, crit = 0, critMulti = 0;
        var weaponBase = attack ? actor.Weapon?.Base : null;
        // Spells without a hit (e.g. Righteous Fire) gain nothing from added flat damage.
        bool hasHit = attack || HasAnyHit(values);
        foreach (var type in hasHit ? Types : [])
        {
            double baseDmg = attack
                ? (weaponBase is null ? (type == "Physical" ? 5 : 0) : weaponBase.GetValueOrDefault(type)) * (L.BaseMultiplier ?? 1)
                : (values.GetValueOrDefault(type + "Min") + values.GetValueOrDefault(type + "Max")) / 2;
            double added = actor.Added(attack, type)
                           + (Sum("BASE", [type + "Min"], hitFlags, hitKeys) + Sum("BASE", [type + "Max"], hitFlags, hitKeys)) / 2;
            double total = baseDmg + added * eff;
            if (total <= 0) continue;
            var names = DamageNames(type);
            double inc = actor.ItemInc(type, hitFlags, false) + Sum("INC", names, hitFlags, hitKeys);
            hit += total * (1 + inc / 100) * More(names, hitFlags, hitKeys);
            incShown = Math.Max(incShown, inc);
        }
        // Weapon elemental damage whose element the API doesn't say: elemental increases only.
        if (weaponBase?.GetValueOrDefault("Elemental") is > 0 and var eleBase)
        {
            string[] names = ["Damage", "ElementalDamage"];
            double inc = actor.ItemInc("Elemental", hitFlags, false) + Sum("INC", names, hitFlags, hitKeys);
            hit += eleBase * (L.BaseMultiplier ?? 1) * (1 + inc / 100) * More(names, hitFlags, hitKeys);
        }
        if (hit > 0)
        {
            string[] speedNames = ["Speed"];
            if (attack)
                speed = (actor.Weapon?.Aps ?? 1.2) * (1 + (L.AttackSpeedMultiplier ?? 0) / 100)
                        * (1 + (actor.AttackSpeedInc + Sum("INC", speedNames, hitFlags, hitKeys)) / 100);
            else
                speed = 1 / (gd.CastTime is > 0 ? gd.CastTime.Value : 1) * (1 + (actor.CastSpeedInc + Sum("INC", speedNames, hitFlags, hitKeys)) / 100);
            speed *= More(speedNames, hitFlags, hitKeys);

            double baseCrit = attack ? actor.Weapon?.Crit ?? 5 : L.Crit ?? 5;
            string[] critNames = ["CritChance"];
            crit = Math.Clamp(baseCrit * (1 + (actor.CritInc(spell) + Sum("INC", critNames, hitFlags, hitKeys)) / 100)
                              * More(critNames, hitFlags, hitKeys), 0, 100);
            critMulti = actor.CritMulti(spell) + Sum("BASE", ["CritMultiplier"], hitFlags, hitKeys);
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
            double inc = actor.ItemInc(type, dotFlags, true) + Sum("INC", names, dotFlags, keys);
            double multi = actor.DotMulti(type) + Sum("BASE", ["DotMultiplier", type + "DotMultiplier"], dotFlags, keys);
            dot += baseDot * (1 + inc / 100) * More(names, dotFlags, keys) * (1 + multi / 100);
            incShown = Math.Max(incShown, inc);
            dotMultiShown = Math.Max(dotMultiShown, multi);
        }

        if (hit <= 0 && dot <= 0) return null; // nothing the data can score (e.g. a buff): fall back
        bool dotSkill = dot > hit;
        return new DamageResult(hit + dot, incShown, dotSkill ? dotMultiShown : critMulti, dotSkill ? 1 : speed, dotSkill ? 0 : crit, 0);
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

    /// <summary>Weapon base damage per type, attacks per second and crit, from its property block (local mods included).</summary>
    static (Dictionary<string, double> Base, double Aps, double Crit)? WeaponOf(EquippedItem? weapon)
    {
        if (weapon is null || !weapon.Props.TryGetValue("Attacks per Second", out var aps)) return null;
        var bases = new Dictionary<string, double>
        {
            ["Physical"] = AvgRange(weapon.Props.GetValueOrDefault("Physical Damage")),
            ["Chaos"] = AvgRange(weapon.Props.GetValueOrDefault("Chaos Damage")),
            ["Elemental"] = AvgRange(weapon.Props.GetValueOrDefault("Elemental Damage")),
        };
        double crit = weapon.Props.TryGetValue("Critical Strike Chance", out var c) ? Items.ItemParser.Numbers(c).DefaultIfEmpty(5).First() : 5;
        return (bases, Items.ItemParser.Numbers(aps).DefaultIfEmpty(1.2).First(), crit);
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
