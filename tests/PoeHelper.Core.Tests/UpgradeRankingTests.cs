using PoeHelper.Core.Build;
using PoeHelper.Core.Pricing;

namespace PoeHelper.Core.Tests;

/// <summary>Find upgrade: resists first, then the tracked stats; capped resists aren't shown.</summary>
public class UpgradeRankingTests(DataFixture fx) : IClassFixture<DataFixture>
{
    static GemInfo Active(string name, string tags) => new(name, 20, 0, 0, 0, [], false, tags.Split(", "), 0);
    static GemInfo Support(string name) => new(name, 20, 0, 0, 0, [], true, ["Support"], 0);

    /// <summary>A cold spell build, elemental resists capped with room to spare, chaos at −30%.</summary>
    static CharacterBuild Build(params string[] ringMods) => new()
    {
        Name = "T", Level = 90, BaseStr = 20, BaseDex = 20, BaseInt = 20,
        Items = new()
        {
            ["BodyArmour"] = new()
            {
                Slot = "BodyArmour", Name = "Body", SocketCount = 3, MaxLinks = 3, Mods = ["+80 to maximum Life"],
                Gems = [Active("Ice Nova", "Spell, AoE, Cold, Nova"), Support("Added Cold Damage Support"), Support("Controlled Destruction Support")],
            },
            ["Ring"] = new() { Slot = "Ring", Name = "Old Ring", Mods = [.. ringMods] },
        },
        TreeStats = ["+150% to all Elemental Resistances", "+30% to Chaos Resistance"],
    };

    static readonly Listing Price = new(1, "divine", 200, DateTime.UtcNow, "Seller#1", true, "");

    static UpgradeCandidate? Eval(CharacterBuild b, UpgradeOptions o, params string[] mods) =>
        UpgradeFinder.Evaluate(GearCompare.CompareEquipped(b, "Ring", new EquippedItem { Slot = "Ring", Name = "New Ring", Mods = [.. mods] }), o, Price);

    static UpgradeOptions Track(bool chaos = false, params string[] keys) => new(TrackedStats.Resolve(keys), chaos);

    [Fact]
    public void Capped_resists_are_hidden_and_chaos_only_shows_when_ticked()
    {
        var b = Build("+20 to maximum Life", "+30% to Fire Resistance");
        var c = Eval(b, Track(false, "life"), "+60 to maximum Life", "+25% to Chaos Resistance")!;
        Assert.NotNull(c);
        Assert.DoesNotContain(c.Rows, r => r.Label.EndsWith(" res")); // fire drops 30 but stays capped; chaos isn't counted
        Assert.Contains("+40 maximum life", c.Gains);

        var withChaos = Eval(b, Track(true, "life"), "+60 to maximum Life", "+25% to Chaos Resistance")!;
        Assert.Contains(withChaos.Rows, r => r.Label == "Chaos res");
        Assert.DoesNotContain(withChaos.Rows, r => r.Label == "Fire res");
        Assert.StartsWith("+25% chaos res", withChaos.Gains);
    }

    [Fact]
    public void Resists_rank_before_tracked_stats_when_chaos_counts()
    {
        var b = Build("+20 to maximum Life");
        var o = Track(true, "life");
        var lifeRing = Eval(b, o, "+90 to maximum Life")!;
        var chaosRing = Eval(b, o, "+30 to maximum Life", "+35% to Chaos Resistance")!;
        var list = new List<UpgradeCandidate> { lifeRing, chaosRing };
        list.Sort(UpgradeFinder.Rank);
        Assert.Same(chaosRing, list[0]);
        // Without chaos, the extra life wins.
        o = Track(false, "life");
        list = [Eval(b, o, "+90 to maximum Life")!, Eval(b, o, "+30 to maximum Life", "+35% to Chaos Resistance")!];
        list.Sort(UpgradeFinder.Rank);
        Assert.Contains("+70 maximum life", list[0].Gains);
    }

    [Fact]
    public void Items_that_gain_nothing_tracked_are_rejected()
    {
        var b = Build("+60 to maximum Life");
        Assert.Null(Eval(b, Track(false, "life"), "+40 to maximum Life", "30% increased Cold Damage"));
        Assert.NotNull(Eval(b, Track(false, "life", TrackedStats.Damage), "+40 to maximum Life", "30% increased Cold Damage"));
    }

    [Fact]
    public void Fixing_chaos_may_cost_at_most_one_roll_of_tracked_stats()
    {
        var b = Build("+100 to maximum Life");
        Assert.NotNull(Eval(b, Track(true, "life"), "+50 to maximum Life", "+35% to Chaos Resistance"));   // −50 life
        Assert.Null(Eval(b, Track(true, "life"), "+35% to Chaos Resistance"));                             // −100 life
    }

    [Fact]
    public void Plan_searches_only_for_tracked_stats()
    {
        var lifeOnly = UpgradeFinder.Plan(Build("+50 to maximum Life"), "Ring", fx.Data, 2, "divine", Track(false, "life"), weighted: true);
        Assert.Equal(["pseudo.pseudo_total_life"], lifeOnly.Weights.Select(w => w.TradeId));

        var dmg = UpgradeFinder.Plan(Build("+50 to maximum Life"), "Ring", fx.Data, 2, "divine", Track(false, TrackedStats.Damage), weighted: true);
        Assert.Contains(dmg.Weights, w => w.Label == "#% increased Cold Damage");
        Assert.DoesNotContain(dmg.Weights, w => w.Label.Contains("Fire") || w.Label.Contains("Attack") || w.TradeId == "pseudo.pseudo_total_life");

        var chaos = UpgradeFinder.Plan(Build("+50 to maximum Life"), "Ring", fx.Data, 2, "divine", Track(true, "life"));
        Assert.Equal("stat.pseudo.pseudo_total_chaos_resistance", chaos.Query["sort"]!.AsObject().First().Key);
        Assert.Contains(chaos.Constraints, c => c.Contains("Maximum life (at least your current item's)"));
    }

    [Fact]
    public void Energy_shield_counts_armour_once_and_jewellery_flat()
    {
        var b = new CharacterBuild
        {
            Name = "T", Level = 90, BaseInt = 100,
            Items = new()
            {
                ["Helm"] = new()
                {
                    Slot = "Helm", Name = "Hat", Mods = ["+50 to maximum Energy Shield"],
                    Props = new(StringComparer.OrdinalIgnoreCase) { ["Energy Shield"] = "200" },
                },
                ["Ring"] = new() { Slot = "Ring", Name = "Ring", Mods = ["+30 to maximum Energy Shield"] },
            },
        };
        // (200 from the helmet's property block + 30 from the ring) × (1 + 20% from 100 Int)
        Assert.Equal(Math.Round(230 * 1.2), BuildCalculator.Compute(b).EnergyShield);
    }
}
