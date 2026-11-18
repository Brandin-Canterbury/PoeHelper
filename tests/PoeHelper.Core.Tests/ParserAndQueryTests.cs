using System.Text.Json.Nodes;
using PoeHelper.Core.Data;
using PoeHelper.Core.Items;
using PoeHelper.Core.Pricing;
using PoeHelper.Core.Stats;

namespace PoeHelper.Core.Tests;

public sealed class DataFixture
{
    public TradeData Data { get; }
    public DataFixture()
    {
        JsonObject Load(string n) => JsonNode.Parse(File.ReadAllText(Path.Combine("Fixtures", "data", n + ".json")))!.AsObject();
        Data = TradeData.FromJson(Load("stats"), Load("items"), Load("static"));
    }
    public static string Item(string name) => File.ReadAllText(Path.Combine("Fixtures", "items", name + ".txt"));
}

public class ParserAndQueryTests(DataFixture fx) : IClassFixture<DataFixture>
{
    [Fact]
    public void Gem_parses_level_quality_corruption_and_queries_exactly()
    {
        var item = ItemParser.Parse(DataFixture.Item("gem_rf"));
        Assert.Equal(ItemRarity.Gem, item.Rarity);
        Assert.Equal("Righteous Fire", item.Name);
        Assert.Equal(20, item.GemLevel);
        Assert.Equal(20, item.Quality);
        Assert.True(item.Corrupted);
        Assert.Equal(98, item.Requirements.Str);
        Assert.Empty(item.Mods);

        var json = PriceQueryBuilder.Build(item, fx.Data).ToSearchJson();
        var q = json["query"]!;
        Assert.Equal("Righteous Fire", q["type"]!.GetValue<string>());
        Assert.Equal("securable", q["status"]!["option"]!.GetValue<string>());
        var misc = q["filters"]!["misc_filters"]!["filters"]!;
        Assert.Equal(20, misc["gem_level"]!["min"]!.GetValue<double>());
        Assert.Equal("true", misc["corrupted"]!["option"]!.GetValue<string>());
    }

    [Fact]
    public void Transfigured_gem_uses_discriminator()
    {
        var item = ItemParser.Parse(DataFixture.Item("gem_transfigured"));
        var q = PriceQueryBuilder.Build(item, fx.Data).ToSearchJson()["query"]!;
        Assert.Equal("Righteous Fire", q["type"]!["option"]!.GetValue<string>());
        Assert.Equal("alt_x", q["type"]!["discriminator"]!.GetValue<string>());
        Assert.Equal("false", q["filters"]!["misc_filters"]!["filters"]!["corrupted"]!["option"]!.GetValue<string>());
    }

    [Fact]
    public void Advanced_rare_ring_reads_tiers_and_strips_ranges()
    {
        var item = ItemParser.Parse(DataFixture.Item("ring_rare_advanced"));
        Assert.True(item.AdvancedText);
        Assert.Equal("Kraken Loop", item.Name);
        Assert.Equal("Ruby Ring", item.BaseType);
        Assert.Equal(83, item.ItemLevel);
        var life = item.Mods.Single(m => m.Text.Contains("Life"));
        Assert.Equal("+75 to maximum Life", life.Text);
        Assert.Equal(2, life.Tier);
        Assert.Equal(ModKind.Implicit, item.Mods[0].Kind);
        Assert.Equal(ModKind.Crafted, item.Mods[^1].Kind);

        var matched = StatMatcher.Match(item, fx.Data);
        Assert.Contains(matched, m => m.Entry.Id == "explicit.stat_3299347043" && m.Value == 75);
        Assert.Contains(matched, m => m.Entry.Id.StartsWith("implicit.") && m.Value == 25);
        Assert.Contains(matched, m => m.Entry.Id.StartsWith("crafted."));

        var query = PriceQueryBuilder.Build(item, fx.Data);
        Assert.Equal("Ruby Ring", query.Type);
        Assert.Equal("accessory.ring", query.Category);
        var resist = query.Filters.Single(f => f.Key == "pseudo.pseudo_total_elemental_resistance");
        Assert.True(resist.Enabled);
        Assert.Equal(Math.Floor((25 + 33 + 38) * 0.9), resist.Min);
        Assert.Contains(query.Filters, f => f.Key == "pseudo.pseudo_total_life" && f.Enabled && f.Min == 67);
        // T1 attack damage counts; the crafted strength does not.
        Assert.Contains(query.Filters, f => f.Label.StartsWith("Adds 20 to 37 Fire Damage to Attacks") && f.Enabled);
        Assert.DoesNotContain(query.Filters, f => f.Enabled && f.Key.Contains("strength"));
    }

    [Fact]
    public void Plain_body_armour_handles_negative_res_reduced_mods_and_links()
    {
        var item = ItemParser.Parse(DataFixture.Item("body_rare_plain"));
        Assert.Equal(6, item.SocketCount);
        Assert.Equal(6, item.MaxLinks);
        Assert.True(item.Corrupted);

        var totals = StatTotals.FromMods(item.Mods);
        Assert.Equal(112, totals.Life);
        Assert.Equal(12 + 42, totals.Fire);
        Assert.Equal(12 - 9, totals.Cold);
        Assert.Equal(12 + 29, totals.Lightning);

        var matched = StatMatcher.Match(item, fx.Data);
        Assert.Contains(matched, m => m.Text == "8% reduced Attribute Requirements" && m.Value == -8);

        var query = PriceQueryBuilder.Build(item, fx.Data);
        Assert.Equal("armour.chest", query.Category);
        Assert.Null(query.Type);
        Assert.Contains(query.Filters, f => f.Key == "socket_filters:links" && f.Min == 6 && f.Enabled);
        var json = query.ToSearchJson()["query"]!;
        Assert.Equal(6, json["filters"]!["socket_filters"]!["filters"]!["links"]!["min"]!.GetValue<double>());
    }

    [Fact]
    public void Currency_goes_to_bulk_exchange()
    {
        var item = ItemParser.Parse(DataFixture.Item("divine_stack"));
        Assert.Equal(5, item.StackSize);
        var query = PriceQueryBuilder.Build(item, fx.Data);
        Assert.Equal(QueryKind.Exchange, query.Kind);
        Assert.Equal("divine", query.ExchangeId);
    }

    [Fact]
    public void Unique_searches_by_name_and_offers_rolls_unticked()
    {
        var item = ItemParser.Parse(DataFixture.Item("unique_6l"));
        var query = PriceQueryBuilder.Build(item, fx.Data);
        Assert.Equal("Kaom's Heart", query.Name);
        Assert.Equal("Glorious Plate", query.Type);
        Assert.Contains(query.Filters, f => f.Label.Contains("500 to maximum Life") && !f.Enabled);
    }

    [Fact]
    public void Magic_flask_resolves_base_type()
    {
        var item = ItemParser.Parse(DataFixture.Item("magic_flask"));
        var query = PriceQueryBuilder.Build(item, fx.Data);
        Assert.Equal("Divine Life Flask", query.Type);
        Assert.Equal("flask", query.Category);
    }
}
