using PoeHelper.Core.Build;

namespace PoeHelper.Core.Tests;

/// <summary>The damage model follows the main skill's tags, so gear is scored for the build, not for RF.</summary>
public class SkillDamageTests
{
    static GemInfo Active(string name, string tags, params string[] mods) =>
        new(name, 20, 0, 0, 0, mods, false, tags.Split(", "), 0);
    static GemInfo Support(string name) => new(name, 20, 0, 0, 0, [], true, ["Support"], 0);

    static CharacterBuild With(GemInfo main, GemInfo[] supports, params EquippedItem[] extra)
    {
        var items = new Dictionary<string, EquippedItem>
        {
            ["BodyArmour"] = new()
            {
                Slot = "BodyArmour", Name = "Body", SocketCount = 6, MaxLinks = 6, Mods = ["+50 to maximum Life"],
                Gems = [main, .. supports],
            },
            ["Boots"] = new()
            {
                Slot = "Boots", Name = "Boots", SocketCount = 2, MaxLinks = 2,
                Gems = [Active("Hatred", "Aura, Spell, AoE, Cold"), Support("Enlighten Support")],
            },
        };
        foreach (var e in extra) items[e.Slot] = e;
        return new CharacterBuild { Name = "T", Level = 90, BaseStr = 20, BaseDex = 20, BaseInt = 20, Items = items, TreeStats = [], JewelMods = [] };
    }

    static double Gain(CharacterBuild b, params string[] ringMods)
    {
        var before = BuildCalculator.Compute(b);
        var after = BuildCalculator.Compute(b.WithItem("Ring", new EquippedItem { Slot = "Ring", Name = "Ring", Mods = [.. ringMods] }));
        return GearCompare.DamageChangePct(before, after);
    }

    [Fact]
    public void Picks_the_most_linked_skill_and_skips_auras()
    {
        var b = With(Active("Vortex", "Spell, AoE, Cold, Duration", "Deals 300 Base Cold Damage per second"),
            [Support("Swift Affliction Support"), Support("Efficacy Support"), Support("Controlled Destruction Support")]);
        Assert.Equal("Vortex", b.Skill!.Name);
        Assert.True(b.Skill.IsDot);
        Assert.True(b.Skill.IsSpell);
        b.MainSkillOverride = "hatred";
        Assert.Equal("Hatred", b.Skill!.Name);
        Assert.False(b.Skill.Auto);
    }

    [Fact]
    public void Cold_dot_build_values_cold_dot_multi_not_fire()
    {
        var b = With(Active("Vortex", "Spell, AoE, Cold, Duration", "Deals 300 Base Cold Damage per second"),
            [Support("Swift Affliction Support"), Support("Efficacy Support")]);
        Assert.True(Gain(b, "+20% to Cold Damage over Time Multiplier") > 5);
        Assert.Equal(0, Gain(b, "+20% to Fire Damage over Time Multiplier"), 3);
        Assert.True(Gain(b, "30% increased Cold Damage") > 5);
        Assert.Equal(0, Gain(b, "30% increased Lightning Damage"), 3);
    }

    [Fact]
    public void Attack_build_values_weapon_flat_speed_and_crit_but_not_spell_damage()
    {
        var weapon = new EquippedItem
        {
            Slot = "Weapon", Name = "Sword",
            Mods = ["100% increased Physical Damage", "Adds 10 to 20 Physical Damage", "20% increased Attack Speed"],
            Props = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Physical Damage"] = "80-160", ["Attacks per Second"] = "1.80", ["Critical Strike Chance"] = "6.00%",
            },
        };
        var b = With(Active("Cyclone", "Attack, AoE, Movement, Channelling, Melee, Physical"),
            [Support("Melee Physical Damage Support"), Support("Brutality Support")], weapon,
            new EquippedItem { Slot = "Helm", Name = "Helm", Gems = [Active("Blood Rage", "Spell, Duration")] });
        Assert.Equal("Cyclone", b.Skill!.Name); // its Movement tag doesn't make it a utility skill
        var s = BuildCalculator.Compute(b);
        Assert.Equal(1.80, s.Speed, 3);                // weapon local attack speed isn't counted twice
        Assert.Equal(0, s.IncreasedDamage);           // nor its local % physical
        Assert.True(Gain(b, "Adds 10 to 20 Physical Damage to Attacks") > 5);
        Assert.True(Gain(b, "10% increased Attack Speed") > 5);
        Assert.True(Gain(b, "+30% to Global Critical Strike Multiplier") > 0);
        Assert.Equal(0, Gain(b, "50% increased Spell Damage"), 3);
    }

    [Fact]
    public void Minion_build_values_minion_damage_and_levels_only()
    {
        var b = With(Active("Raise Zombie", "Minion, Spell, Physical"),
            [Support("Minion Damage Support"), Support("Melee Physical Damage Support")]);
        Assert.True(b.Skill!.IsMinion);
        Assert.True(Gain(b, "Minions deal 20% increased Damage") > 5);
        Assert.True(Gain(b, "+1 to Level of all Minion Skill Gems") > 5);
        Assert.Equal(0, Gain(b, "50% increased Spell Damage"), 3);
        Assert.Equal(0, Gain(b, "+1 to Level of all Fire Spell Skill Gems"), 3);
    }
}
