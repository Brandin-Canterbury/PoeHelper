using PoeHelper.Core.Build;
using PoeHelper.Core.Items;

namespace PoeHelper.Core.Tests;

public class BuildTests
{
    static GemInfo Gem(string name, int str = 0, int dex = 0, int intl = 0, bool support = false, params string[] mods) =>
        new(name, 20, str, dex, intl, mods, support);

    static CharacterBuild Rf(params EquippedItem[] extra)
    {
        var items = new Dictionary<string, EquippedItem>
        {
            ["BodyArmour"] = new()
            {
                Slot = "BodyArmour", Name = "Old Body", ReqStr = 100, SocketCount = 6, MaxLinks = 6,
                Mods = ["+100 to maximum Life", "+40% to Fire Resistance"],
                Gems =
                [
                    Gem("Righteous Fire", 98, 0, 68, false,
                        "Deals 70% of your Maximum Life as Base Fire Damage per second",
                        "You Burn for 90% of your Maximum Life per second as Fire Damage"),
                    Gem("Efficacy Support", 0, 0, 114, true), Gem("Burning Damage Support", 72, 0, 50, true),
                    Gem("Elemental Focus Support", 0, 0, 114, true), Gem("Swift Affliction Support", 0, 114, 0, true),
                    Gem("Increased Area of Effect Support", 0, 0, 114, true),
                ],
            },
            ["Boots"] = new()
            {
                Slot = "Boots", Name = "Boots", SocketCount = 1, MaxLinks = 1,
                Mods = ["+30% to Cold Resistance", "+30% to Lightning Resistance"],
                Gems = [Gem("Purity of Elements", 68, 0, 98, false, "You and nearby allies gain +34% to all Elemental Resistances")],
            },
        };
        foreach (var e in extra) items[e.Slot] = e;
        return new CharacterBuild
        {
            Name = "Test", Level = 90, BaseStr = 32, BaseDex = 14, BaseInt = 14, Items = items,
            TreeStats = ["+100 to Strength", "+100 to Intelligence", "+100 to Dexterity", "10% increased maximum Life",
                         "Regenerate 2% of Life per second", "+1% to maximum Fire Resistance",
                         "Modifiers to Fire Resistance also apply to Cold and Lightning Resistances at 50% of their Value"],
        };
    }

    [Fact]
    public void Computes_life_resists_with_penalty_aura_and_fire_conversion()
    {
        var s = BuildCalculator.Compute(Rf());
        // str 132 → +66 life; (38 + 1080 + 100 + 66) × 1.10
        Assert.Equal(Math.Round((38 + 12 * 90 + 100 + 66) * 1.10), s.Life);
        Assert.Equal(40 + 34 - 60, s.Fire.Total);          // body + Purity − penalty
        Assert.Equal(76, s.Fire.Max);
        Assert.Equal(30 + 34 + (40 + 34) * 0.5 - 60, s.Cold.Total);
        Assert.True(s.HasRf);
        Assert.Equal(s.Life * 0.9 * (1 - 14 / 100.0), s.RfDegen, 3);
        Assert.Equal(114, s.IntNeed.Need);
    }

    [Fact]
    public void Swapping_to_fewer_sockets_drops_gems_and_warns_about_rf_links()
    {
        var item = ItemParser.Parse("""
            Item Class: Body Armours
            Rarity: Rare
            Grim Shell
            Glorious Plate
            --------
            Sockets: R-R-R-R
            --------
            Item Level: 84
            --------
            +150 to maximum Life
            +40% to Fire Resistance
            """);
        var c = Assert.Single(GearCompare.Compare(Rf(), item));
        Assert.Equal(Sentiment.Bad, c.Overall);
        Assert.Contains(c.Warnings, w => w.Contains("wouldn't fit"));
        Assert.Contains(c.Warnings, w => w.Contains("RF setup"));
    }

    [Fact]
    public void Ring_compares_against_both_ring_slots_and_flags_resist_falling_under_cap()
    {
        var ring = new EquippedItem { Slot = "Ring", Name = "Res Ring", Mods = ["+45% to Cold Resistance", "+45% to Lightning Resistance"] };
        var build = Rf(ring, new EquippedItem { Slot = "Ring2", Name = "Other", Mods = [] });
        var item = ItemParser.Parse("""
            Item Class: Rings
            Rarity: Rare
            Life Loop
            Coral Ring
            --------
            Item Level: 84
            --------
            +60 to maximum Life
            """);
        var result = GearCompare.Compare(build, item);
        Assert.Equal(2, result.Count);
        var vsRes = result.Single(r => r.Slot == "Ring");
        Assert.Equal(Sentiment.Bad, vsRes.Overall);
        Assert.Contains(vsRes.Warnings, w => w.StartsWith("Cold res drops below cap"));
        Assert.Equal(Sentiment.Better, result.Single(r => r.Slot == "Ring2").Overall);
    }

    [Fact]
    public void Strips_api_markup()
    {
        Assert.Equal("Allocates Golem's Blood", CharacterBuild.StripMarkup("Allocates [golem's_blood1088|Golem's Blood]"));
        Assert.Equal("Immune to Ignite", CharacterBuild.StripMarkup("Immune to [Ignite]"));
    }

    [Fact]
    public void Life_degen_mods_and_negative_resists_count_against_you()
    {
        var t = PoeHelper.Core.Stats.StatTotals.FromTexts(["Lose 64 Life per second", "Regenerate 100 Life per second", "-66% to Cold Resistance"]);
        Assert.Equal(36, t.LifeRegenFlat);
        Assert.Equal(-66, t.Cold);
    }
}
