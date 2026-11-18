using System.Globalization;
using System.Text.Json.Nodes;

namespace PoeHelper.Core.Pricing;

public sealed record Listing(
    double Amount, string Currency, double? Chaos, DateTime Indexed, string Account, bool InstantBuyout,
    string Summary, bool Outlier = false, int Stock = 0);

public enum Confidence { None, Low, Medium, High }

public sealed class PriceAnalysis
{
    public required List<Listing> Listings { get; init; }
    public int TotalListed { get; init; }
    public double? QuickSellChaos { get; init; }
    public double? MarketChaos { get; init; }
    public Confidence Confidence { get; init; }
    public string Headline { get; init; } = "";
    public List<string> Notes { get; } = [];
    public double DivineRate { get; init; }
    public bool PreferDivines { get; init; }

    public string Format(double chaos) => CurrencyRates.Format(chaos, DivineRate, PreferDivines);
}

/// <summary>
/// Turns raw listings into a recommendation: drops leading bait/price-fix listings, takes the cheapest real
/// listing as the "sell fast" price and the median of the next few as "market", and explains the market
/// (depth, spread, stale listings) in plain notes.
/// </summary>
public static class PriceAnalyzer
{
    const double OutlierRatio = 0.6;

    public static Listing ParseListing(JsonObject result, CurrencyRates rates)
    {
        var listing = result["listing"]!;
        var price = listing["price"];
        double amount = price?["amount"]?.GetValue<double>() ?? 0;
        string currency = price?["currency"]?.GetValue<string>() ?? "?";
        var indexed = DateTime.TryParse(listing["indexed"]?.GetValue<string>(), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : DateTime.UtcNow;
        bool instant = listing["method"]?.GetValue<string>() == "psapi" && listing["fee"] is not null;
        return new Listing(amount, currency, rates.ToChaos(amount, currency), indexed,
            listing["account"]?["name"]?.GetValue<string>() ?? "?", instant, Summarize(result["item"] as JsonObject));
    }

    /// <summary>Short per-listing description so you can see *why* one is cheaper (level, quality, links...).</summary>
    static string Summarize(JsonObject? item)
    {
        if (item is null) return "";
        var parts = new List<string>();
        foreach (var p in item["properties"]?.AsArray() ?? [])
        {
            var name = p?["name"]?.GetValue<string>();
            var val = p?["values"]?.AsArray().FirstOrDefault()?.AsArray().FirstOrDefault()?.GetValue<string>();
            if (name is "Level" or "Quality" && val is not null) parts.Add($"{(name == "Level" ? "L" : "Q")}{val.Replace("+", "").Replace("%", "").Replace(" (Max)", "")}");
        }
        if (item["corrupted"]?.GetValue<bool>() == true) parts.Add("corrupted");
        var sockets = item["sockets"]?.AsArray();
        if (sockets is { Count: > 0 })
        {
            int links = sockets.GroupBy(s => s?["group"]?.GetValue<int>()).Max(g => g.Count());
            if (links >= 5) parts.Add($"{links}L");
        }
        return string.Join(" ", parts);
    }

    public static PriceAnalysis Analyze(IReadOnlyList<Listing> raw, int total, CurrencyRates rates, int stackSize = 1, bool bulk = false, bool chaosOnly = false)
    {
        var priced = raw.Where(l => l.Chaos is > 0).OrderBy(l => l.Chaos).ToList();
        var unpriced = raw.Where(l => l.Chaos is null or <= 0).ToList();
        var marked = MarkLeadingOutliers(priced);
        var real = marked.Where(l => !l.Outlier).ToList();

        double? quick = real.Count > 0 ? real[0].Chaos : null;
        // Lower median of the cheapest five real listings: conservative, since you're trying to sell.
        double? market = real.Count > 0 ? LowerMedian(real.Take(Math.Min(5, real.Count)).Select(l => l.Chaos!.Value)) : null;

        int depth = Math.Max(total, raw.Count);
        var confidence = depth switch
        {
            0 => Confidence.None,
            < 5 => Confidence.Low,
            < 20 => Confidence.Medium,
            _ => Confidence.High,
        };
        // Listings priced in divines mostly → answer in divines.
        int divCount = raw.Count(l => l.Currency == "divine");
        bool preferDiv = !chaosOnly && market is { } mk && (mk >= rates.ChaosPerDivine * 0.9 || divCount > raw.Count / 2);

        var a = new PriceAnalysis
        {
            Listings = [.. marked, .. unpriced],
            TotalListed = total,
            QuickSellChaos = quick,
            MarketChaos = market,
            Confidence = confidence,
            DivineRate = rates.ChaosPerDivine,
            PreferDivines = preferDiv,
            Headline = BuildHeadline(quick, market, depth, rates, preferDiv, bulk, stackSize),
        };
        AddNotes(a, marked, real, rates, stackSize, bulk);
        return a;
    }

    static string BuildHeadline(double? quick, double? market, int depth, CurrencyRates rates, bool div, bool bulk, int stack)
    {
        if (depth == 0 || market is null)
            return "No comparable listings.";
        string F(double c) => CurrencyRates.Format(CurrencyRates.Nice(c, rates.ChaosPerDivine, div), rates.ChaosPerDivine, div);
        if (bulk)
            return div || market.Value < rates.ChaosPerDivine
                ? $"Worth about {CurrencyRates.FormatUnit(market.Value, rates.ChaosPerDivine)} each"
                : $"Worth about {Math.Round(market.Value):0}c each";
        if (market < 3)
            return $"Low value (~{F(market.Value)}). Vendor it or sell in bulk.";
        if (quick is { } q && market / q > 1.15)
            return $"List at {F(market.Value)}; {F(q)} sells fast";
        return $"List at about {F(market.Value)}";
    }

    static void AddNotes(PriceAnalysis a, List<Listing> marked, List<Listing> real, CurrencyRates rates, int stack, bool bulk)
    {
        int bait = marked.Count(l => l.Outlier);
        if (bait > 0)
            a.Notes.Add($"Ignored {bait} suspiciously cheap listing{(bait > 1 ? "s" : "")} (likely price-fixing bait or a mistake).");

        if (a.TotalListed == 0)
        {
            a.Notes.Add("Nothing similar is listed with these stats. The search may be too strict: untick a stat and search again.");
            a.Notes.Add("A rare combination can be worth more than you'd guess, so don't vendor it on this result alone.");
            return;
        }
        if (a.Confidence == Confidence.Low)
            a.Notes.Add($"Thin market: only {a.TotalListed} listed. Treat the price as a starting point and adjust if it doesn't sell in a day.");
        else if (a.Confidence == Confidence.High && !bulk)
            a.Notes.Add($"{a.TotalListed} similar listed: a deep market, so the price is reliable.");

        if (real.Count >= 4)
        {
            double spread = real[Math.Min(4, real.Count - 1)].Chaos!.Value / real[0].Chaos!.Value;
            if (spread > 2)
                a.Notes.Add("Prices vary a lot between similar items, so rolls matter. Compare your values with the listings below.");
        }

        var cheapest = real.Take(3).ToList();
        if (cheapest.Count > 0)
        {
            double ageDays = cheapest.Average(l => (DateTime.UtcNow - l.Indexed).TotalDays);
            if (ageDays > 3)
                a.Notes.Add($"The cheapest listings have sat unsold for ~{ageDays:0} days, so buyers think they're overpriced. Undercut them.");
            else if (ageDays < 0.25 && a.TotalListed >= 4)
                a.Notes.Add("The cheapest listings are fresh, so the market is moving. Recheck before you list.");
        }

        if (stack > 1 && a.MarketChaos is { } unit)
            a.Notes.Add($"Your stack of {stack} ≈ {CurrencyRates.Format(unit * stack, rates.ChaosPerDivine, unit * stack >= rates.ChaosPerDivine)}.");

        if (rates.IsFallback)
            a.Notes.Add("Divine rate unavailable, so chaos conversions are approximate.");
    }

    /// <summary>
    /// A listing is bait when it costs less than 60% of the median of the next five. Instant-buyout listings
    /// are never bait: anyone can buy them, so a cheap one that sits unsold is the real market price.
    /// </summary>
    static List<Listing> MarkLeadingOutliers(List<Listing> sorted)
    {
        var result = sorted.ToList();
        for (int i = 0; i < result.Count - 1; i++)
        {
            if (result[i].InstantBuyout) break;
            var next = result.Skip(i + 1).Take(5).Select(l => l.Chaos!.Value).ToList();
            if (next.Count < 2) break;
            if (result[i].Chaos!.Value < OutlierRatio * Median(next))
                result[i] = result[i] with { Outlier = true };
            else break;
        }
        return result;
    }

    public static double LowerMedian(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        return v.Count == 0 ? 0 : v[(v.Count - 1) / 2];
    }

    public static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        if (v.Count == 0) return 0;
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }
}
