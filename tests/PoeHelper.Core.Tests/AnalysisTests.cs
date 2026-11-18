using PoeHelper.Core.Pricing;

namespace PoeHelper.Core.Tests;

public class AnalysisTests
{
    static Listing L(double amount, string cur = "chaos", double daysOld = 0.5, bool instant = false) =>
        new(amount, cur, cur == "chaos" ? amount : amount * 200, DateTime.UtcNow.AddDays(-daysOld), "acct", instant, "");

    [Fact]
    public void Instant_buyout_listings_are_never_bait()
    {
        var a = PriceAnalyzer.Analyze([L(5, instant: true), L(50, instant: true), L(55, instant: true), L(60, instant: true)], 4, CurrencyRates.Fixed(200));
        Assert.DoesNotContain(a.Listings, l => l.Outlier);
        Assert.Equal(5, a.QuickSellChaos);
    }

    [Fact]
    public void Bait_listing_is_ignored_and_market_is_median()
    {
        var rates = CurrencyRates.Fixed(200);
        var a = PriceAnalyzer.Analyze([L(5), L(50), L(55), L(60), L(62), L(70), L(90)], 30, rates);
        Assert.True(a.Listings[0].Outlier);
        Assert.Equal(50, a.QuickSellChaos);
        Assert.Equal(60, a.MarketChaos);
        Assert.Equal(Confidence.High, a.Confidence);
        Assert.Contains(a.Notes, n => n.Contains("suspiciously cheap"));
    }

    [Fact]
    public void Divine_priced_items_are_answered_in_divines()
    {
        var rates = CurrencyRates.Fixed(200);
        var a = PriceAnalyzer.Analyze([L(4, "divine"), L(4, "divine"), L(4.5, "divine"), L(5, "divine")], 4, rates);
        Assert.True(a.PreferDivines);
        Assert.Contains("4 div", a.Headline);
        Assert.DoesNotContain("800c", a.Headline);
    }

    [Fact]
    public void Formatting()
    {
        Assert.Equal("4.5 div", CurrencyRates.Format(900, 200, true));
        Assert.Equal("85c", CurrencyRates.Format(85, 200, false));
        Assert.Equal("12 div", CurrencyRates.Format(2400, 200, true));
        Assert.Equal("10 per chaos", CurrencyRates.FormatUnit(0.1, 200));
    }

    [Fact]
    public void Empty_result_says_loosen_filters()
    {
        var a = PriceAnalyzer.Analyze([], 0, CurrencyRates.Fixed(200));
        Assert.Equal(Confidence.None, a.Confidence);
        Assert.Contains(a.Notes, n => n.Contains("untick"));
    }

    [Fact]
    public void Stale_cheapest_listings_are_flagged()
    {
        var a = PriceAnalyzer.Analyze([L(40, daysOld: 6), L(42, daysOld: 5), L(45, daysOld: 7), L(46)], 4, CurrencyRates.Fixed(200));
        Assert.Contains(a.Notes, n => n.Contains("unsold"));
    }
}
