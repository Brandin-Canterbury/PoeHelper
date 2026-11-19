using PoeHelper.Core.Build;

namespace PoeHelper.Core.Tests;

public class UpgradePlanTests(DataFixture fx) : IClassFixture<DataFixture>
{
    static CharacterBuild Build() => new()
    {
        Name = "T", Level = 90, BaseStr = 32, BaseDex = 14, BaseInt = 14,
        Items = new()
        {
            ["Ring"] = new EquippedItem { Slot = "Ring", Name = "Old Ring", Mods = ["+50 to maximum Life", "+40% to Fire Resistance"] },
            ["Amulet"] = new EquippedItem { Slot = "Amulet", Name = "Amu", Mods = ["+20% to Fire Damage over Time Multiplier"] },
        },
        TreeStats = ["+60% to Fire Resistance", "50% increased maximum Life"],
    };

    [Fact]
    public void Anonymous_plan_sorts_by_one_stat_without_weight_group()
    {
        var plan = UpgradeFinder.Plan(Build(), "Ring", fx.Data, 2, "divine");
        Assert.False(plan.Weighted);
        Assert.Single(plan.Query["query"]!["stats"]!.AsArray());
        Assert.StartsWith("stat.", plan.Query["sort"]!.AsObject().First().Key);
        // Without the ring, fire drops to 0 + 60 − 60 = 0: the ring must bring the 75 back.
        Assert.Contains(plan.Constraints, c => c.Contains("fire res"));
    }

    [Fact]
    public void Logged_in_plan_uses_a_weighted_group_with_current_item_as_floor()
    {
        var plan = UpgradeFinder.Plan(Build(), "Ring", fx.Data, 2, "divine", weighted: true);
        Assert.True(plan.Weighted);
        var groups = plan.Query["query"]!["stats"]!.AsArray();
        Assert.Equal(2, groups.Count);
        var weight = groups[0]!;
        Assert.Equal("weight", weight["type"]!.GetValue<string>());
        Assert.InRange(weight["filters"]!.AsArray().Count, 1, UpgradeFinder.MaxWeightedStats);
        Assert.True(weight["value"]!["min"]!.GetValue<double>() >= 50, weight.ToJsonString()); // at least the old ring's 50 life
        Assert.Equal("desc", plan.Query["sort"]!["statgroup.0"]!.GetValue<string>());
    }
}
