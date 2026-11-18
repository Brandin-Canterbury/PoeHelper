using System.Globalization;
using System.Text.Json.Nodes;
using PoeHelper.Core.Trade;

namespace PoeHelper.Core.Pricing;

/// <summary>
/// Chaos-per-divine (the only conversion most prices need), read from the bulk exchange and cached.
/// Prices in other currencies are shown as listed but left out of chaos maths.
/// </summary>
public sealed class CurrencyRates
{
    public const double FallbackDivine = 200;
    public double ChaosPerDivine { get; private set; } = FallbackDivine;
    public bool IsFallback { get; private set; } = true;
    public DateTime FetchedAt { get; private set; } = DateTime.MinValue;
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);

    public static CurrencyRates Fixed(double chaosPerDivine) =>
        new() { ChaosPerDivine = chaosPerDivine, IsFallback = false, FetchedAt = DateTime.UtcNow };

    public async Task EnsureFreshAsync(TradeClient client, string league, CancellationToken ct = default)
    {
        if (!IsFallback && DateTime.UtcNow - FetchedAt < MaxAge) return;
        var query = new JsonObject
        {
            ["query"] = new JsonObject
            {
                ["status"] = new JsonObject { ["option"] = "online" },
                ["have"] = new JsonArray("chaos"),
                ["want"] = new JsonArray("divine"),
            },
            ["sort"] = new JsonObject { ["have"] = "asc" },
            ["engine"] = "new",
        };
        var json = await client.ExchangeAsync(league, query, ct);
        var ratios = ExchangeOffers(json).Select(o => o.ChaosPerUnit(1)).Where(r => r > 0).ToList();
        if (ratios.Count == 0) return;
        // Drop bait and keep the cheapest real cluster: median of the ratios after leading outliers.
        ratios.Sort();
        var real = ratios.SkipWhile((r, i) => i + 1 < ratios.Count && r < 0.6 * PriceAnalyzer.Median(ratios.Skip(i + 1).Take(5))).Take(5);
        // Same rule as item prices (lower median of the five cheapest real offers), so a divine's own
        // price check and every conversion agree.
        ChaosPerDivine = Math.Round(PriceAnalyzer.LowerMedian(real), 1);
        IsFallback = false;
        FetchedAt = DateTime.UtcNow;
    }

    public double? ToChaos(double amount, string currency) => currency switch
    {
        "chaos" => amount,
        "divine" => amount * ChaosPerDivine,
        _ => null,
    };

    public sealed record Offer(string PayCurrency, double PayAmount, string ItemCurrency, double ItemAmount, int Stock,
        string Account, DateTime Indexed)
    {
        /// <summary>Price of one item, in chaos.</summary>
        public double ChaosPerUnit(double chaosPerDivine) => PayCurrency switch
        {
            "chaos" => PayAmount / ItemAmount,
            "divine" => PayAmount * chaosPerDivine / ItemAmount,
            _ => 0,
        };
    }

    public static IEnumerable<Offer> ExchangeOffers(JsonObject json)
    {
        foreach (var (_, node) in json["result"]?.AsObject() ?? [])
        {
            var listing = node?["listing"];
            if (listing is null) continue;
            var account = listing["account"]?["name"]?.GetValue<string>() ?? "?";
            DateTime.TryParse(listing["indexed"]?.GetValue<string>(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal, out var indexed);
            foreach (var o in listing["offers"]?.AsArray() ?? [])
            {
                var ex = o?["exchange"]; var it = o?["item"];
                if (ex is null || it is null) continue;
                yield return new Offer(
                    ex["currency"]!.GetValue<string>(), ex["amount"]!.GetValue<double>(),
                    it["currency"]!.GetValue<string>(), it["amount"]!.GetValue<double>(),
                    it["stock"]?.GetValue<int>() ?? 0, account, indexed);
            }
        }
    }

    /// <summary>"4.5 div" or "85c". Never turns a divine price into a big chaos number.</summary>
    public static string Format(double chaos, double chaosPerDivine, bool preferDivines)
    {
        if (preferDivines && chaos >= chaosPerDivine * 0.5)
        {
            double div = chaos / chaosPerDivine;
            string d = div >= 10 ? Math.Round(div).ToString("0", CultureInfo.InvariantCulture)
                                 : (Math.Round(div * 10) / 10).ToString("0.#", CultureInfo.InvariantCulture);
            return $"{d} div";
        }
        return chaos >= 10 ? $"{Math.Round(chaos):0}c" : $"{chaos.ToString("0.#", CultureInfo.InvariantCulture)}c";
    }

    /// <summary>Rounds to the steps people actually list at: 0.5 div under 10 div, whole div above; 1c / 5c in chaos.</summary>
    public static double Nice(double chaos, double chaosPerDivine, bool preferDivines)
    {
        if (preferDivines && chaos >= chaosPerDivine * 0.5)
        {
            double div = chaos / chaosPerDivine;
            double step = div < 10 ? 0.5 : 1;
            return Math.Max(step, Math.Round(div / step, MidpointRounding.AwayFromZero) * step) * chaosPerDivine;
        }
        if (chaos < 1) return chaos;
        return chaos < 100 ? Math.Round(chaos) : Math.Round(chaos / 5) * 5;
    }

    /// <summary>Unit prices for bulk items: "3.2c", "12 per chaos", "1.4 div".</summary>
    public static string FormatUnit(double chaosEach, double chaosPerDivine)
    {
        if (chaosEach <= 0) return "?";
        if (chaosEach < 1) return $"{(1 / chaosEach).ToString("0.#", CultureInfo.InvariantCulture)} per chaos";
        return Format(chaosEach, chaosPerDivine, chaosEach >= chaosPerDivine);
    }
}
