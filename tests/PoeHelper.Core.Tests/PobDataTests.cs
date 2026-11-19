using PoeHelper.Core.Build;
using PoeHelper.Core.Data;

namespace PoeHelper.Core.Tests;

/// <summary>The damage model reads Path of Building's gem data: real base damage per level and support multipliers.</summary>
public class PobDataTests
{
    static GemInfo Active(string name, int level = 20, string tags = "Spell") => new(name, level, 0, 0, 0, [], false, tags.Split(", "), 0);
    static GemInfo Support(string name) => new(name, 20, 0, 0, 0, [], true, ["Support"], 0);

    static CharacterBuild With(GemInfo main, GemInfo[] supports, string[]? bodyMods = null, string[]? tree = null) => new()
    {
        Name = "T", Level = 90, BaseStr = 20, BaseDex = 20, BaseInt = 20,
        Items = new()
        {
            ["BodyArmour"] = new()
            {
                Slot = "BodyArmour", Name = "Body", SocketCount = 6, MaxLinks = 6, Mods = [.. bodyMods ?? []],
                Gems = [main, .. supports],
            },
        },
        TreeStats = [.. tree ?? []],
    };

    static double Index(CharacterBuild b) => BuildCalculator.Compute(b).DamageIndex;

    [Fact]
    public void Data_is_embedded_and_matches_pob()
    {
        var fb = SkillData.Instance.Get("Fireball")!;
        Assert.Contains("Path of Building", SkillData.Instance.Source);
        Assert.Equal(0.75, fb.CastTime);
        var l20 = fb.Level(20)!;
        Assert.Equal(1883, l20.Skill["FireMin"]);
        Assert.Equal(2825, l20.Skill["FireMax"]);
        var ef = SkillData.Instance.Get("Elemental Focus Support")!.Level(20)!;
        Assert.Contains(ef.Mods, m => m is { Name: "ElementalDamage", Type: "MORE", Value: 34 });
    }

    [Fact]
    public void Support_more_multipliers_apply_to_matching_damage()
    {
        var plain = Index(With(Active("Fireball", tags: "Spell, Projectile, AoE, Fire"), []));
        var ef = Index(With(Active("Fireball", tags: "Spell, Projectile, AoE, Fire"), [Support("Elemental Focus Support")]));
        Assert.Equal(1.34, ef / plain, 3);
        // Swift Affliction is more damage over time: no effect on Fireball's hit.
        var sa = Index(With(Active("Fireball", tags: "Spell, Projectile, AoE, Fire"), [Support("Swift Affliction Support")]));
        Assert.Equal(1.0, sa / plain, 3);
    }

    [Fact]
    public void Gem_level_changes_base_damage_from_the_level_table()
    {
        var l20 = Index(With(Active("Fireball", 20), []));
        var l21 = Index(With(Active("Fireball", 21), []));
        var fb = SkillData.Instance.Get("Fireball")!;
        double Avg(int l) => (fb.Level(l)!.Skill["FireMin"] + fb.Level(l)!.Skill["FireMax"]) / 2;
        Assert.Equal(Avg(21) / Avg(20), l21 / l20, 3);
    }

    [Fact]
    public void Righteous_fire_scales_with_life_es_burning_but_not_spell_damage_or_controlled_destruction()
    {
        var rf = Active("Righteous Fire", tags: "Spell, AoE, Fire");
        double baseIdx = Index(With(rf, []));
        Assert.Equal(baseIdx, Index(With(rf, [], tree: ["50% increased Spell Damage"])), 3);            // PoB: RF's burn isn't spell damage
        Assert.Equal(baseIdx, Index(With(rf, [Support("Controlled Destruction Support")])), 3);       // more *spell* damage
        Assert.True(Index(With(rf, [], tree: ["50% increased Burning Damage"])) > baseIdx * 1.4);
        Assert.True(Index(With(rf, [Support("Burning Damage Support")])) > baseIdx * 1.3);
        Assert.True(Index(With(rf, [], bodyMods: ["+100 to maximum Life"])) > baseIdx);
        Assert.True(Index(With(rf, [], bodyMods: ["+100 to maximum Energy Shield"])) > baseIdx);
    }

    [Fact]
    public void Righteous_fire_variants_do_not_borrow_rf_maths()
    {
        var rfad = With(Active("Righteous Fire of Arcane Devotion", tags: "Spell, AoE, Fire"), []);
        Assert.Null(BuildCalculator.Compute(rfad).Formula);
    }
}
