using PoeHelper.Core.Build;
using PoeHelper.Core.Data;

namespace PoeHelper.Core.Tests;

/// <summary>Minion gems use Path of Building's minion model: level, ally damage table, their own skill, × count.</summary>
public class MinionDataTests
{
    static GemInfo Active(string name, int level = 20) => new(name, level, 0, 0, 0, [], false, ["Minion", "Spell"], 0);
    static GemInfo Support(string name) => new(name, 20, 0, 0, 0, [], true, ["Support"], 0);

    static CharacterBuild With(GemInfo main, GemInfo[] supports, params string[] tree) => new()
    {
        Name = "T", Level = 90, BaseStr = 20, BaseDex = 20, BaseInt = 20,
        Items = new()
        {
            ["BodyArmour"] = new() { Slot = "BodyArmour", Name = "Body", SocketCount = 6, MaxLinks = 6, Gems = [main, .. supports] },
        },
        TreeStats = [.. tree],
    };

    static BuildStats Stats(CharacterBuild b) => BuildCalculator.Compute(b);

    [Fact]
    public void Zombies_count_six_at_level_20_and_hit_from_the_ally_damage_table()
    {
        var s = Stats(With(Active("Raise Zombie"), []));
        Assert.Equal(6, s.MinionCount);
        var data = SkillData.Instance;
        var zombie = data.Minions["RaisedZombie"];
        // One zombie: ally damage at minion level 70 × 1.65 × attack time, at 1/attack time per second, 5% crit at 150%.
        double perHit = data.AllyDamageAt(70) * zombie.Damage * zombie.AttackTime;
        double perZombie = perHit / zombie.AttackTime * (1 + 0.05 * 0.5);
        Assert.Equal(perZombie * 6, s.DamageIndex, 0);
    }

    [Fact]
    public void Minion_supports_and_minion_stats_reach_the_minions()
    {
        double plain = Stats(With(Active("Raise Zombie"), [])).DamageIndex;
        Assert.Equal(1.39, Stats(With(Active("Raise Zombie"), [Support("Minion Damage Support")])).DamageIndex / plain, 3);
        Assert.Equal(1.30, Stats(With(Active("Raise Zombie"), [], "Minions deal 30% increased Damage")).DamageIndex / plain, 3);
        Assert.True(Stats(With(Active("Raise Zombie"), [Support("Melee Physical Damage Support")])).DamageIndex > plain * 1.2);
        // The player's own spell damage doesn't reach zombies.
        Assert.Equal(plain, Stats(With(Active("Raise Zombie"), [], "50% increased Spell Damage")).DamageIndex, 3);
    }

    [Fact]
    public void Gem_level_raises_minion_level_and_count()
    {
        var s20 = Stats(With(Active("Raise Zombie", 20), []));
        var s21 = Stats(With(Active("Raise Zombie", 21), []));
        var data = SkillData.Instance;
        int req21 = data.Get("Raise Zombie")!.Level(21)!.Req;
        double perZombie20 = s20.DamageIndex / s20.MinionCount, perZombie21 = s21.DamageIndex / s21.MinionCount;
        Assert.Equal(data.AllyDamageAt(req21) / data.AllyDamageAt(70), perZombie21 / perZombie20, 3);
    }

    [Fact]
    public void Raging_spirits_get_their_own_more_attack_speed()
    {
        var s = Stats(With(Active("Summon Raging Spirit"), []));
        var spirit = SkillData.Instance.Minions["SummonedRagingSpirit"];
        Assert.Equal(1 / spirit.AttackTime * 1.4, s.Speed, 3);
    }
}
