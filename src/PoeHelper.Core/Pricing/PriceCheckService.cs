using PoeHelper.Core.Data;
using PoeHelper.Core.Items;
using PoeHelper.Core.Trade;

namespace PoeHelper.Core.Pricing;

public sealed class PriceCheckResult
{
    public required ParsedItem Item { get; init; }
    public required PriceQuery Query { get; init; }
    public required PriceAnalysis Analysis { get; init; }
    public string? SearchId { get; init; }
    public string League { get; init; } = "";

    /// <summary>Opens the same search on the trade site.</summary>
    public string TradeUrl => Query.Kind == QueryKind.Exchange
        ? $"{TradeClient.BaseUrl}/trade/exchange/{Uri.EscapeDataString(League)}"
        : $"{TradeClient.BaseUrl}/trade/search/{Uri.EscapeDataString(League)}/{SearchId}";
}

/// <summary>Item text → query → listings → analysis. One search (+ ≤2 fetches) or one exchange call per run.</summary>
public sealed class PriceCheckService(TradeClient client, TradeData data, CurrencyRates rates)
{
    public int ListingsToFetch { get; set; } = 20;

    public PriceQuery BuildQuery(string itemText, string status) =>
        PriceQueryBuilder.Build(ItemParser.Parse(itemText), data, status);

    public async Task<PriceCheckResult> RunAsync(PriceQuery query, string league, CancellationToken ct = default)
    {
        await rates.EnsureFreshAsync(client, league, ct);

        if (query.Kind == QueryKind.Exchange)
        {
            var json = await client.ExchangeAsync(league, query.ToExchangeJson(), ct);
            var listings = CurrencyRates.ExchangeOffers(json)
                .Where(o => o.Stock >= o.ItemAmount && o.PayCurrency != o.ItemCurrency)
                .Select(o => new Listing(o.PayAmount / o.ItemAmount, o.PayCurrency, o.ChaosPerUnit(rates.ChaosPerDivine),
                    o.Indexed, o.Account, false, $"{o.PayAmount:0.##} {o.PayCurrency} : {o.ItemAmount:0.##}, stock {o.Stock}", Stock: o.Stock))
                .ToList();
            int total = json["total"]?.GetValue<int>() ?? listings.Count;
            return new PriceCheckResult
            {
                Item = query.Item, Query = query, League = league,
                Analysis = PriceAnalyzer.Analyze(listings, total, rates, query.ExchangeId == "divine" ? 1 : query.Item.StackSize, bulk: true,
                    chaosOnly: query.ExchangeId == "divine"),
            };
        }

        var search = await client.SearchAsync(league, query.ToSearchJson(), ct);
        var fetched = search.Ids.Count == 0 ? [] : await client.FetchAsync(search, ListingsToFetch, ct);
        var parsed = fetched.Select(r => PriceAnalyzer.ParseListing(r, rates)).ToList();
        return new PriceCheckResult
        {
            Item = query.Item, Query = query, League = league, SearchId = search.Id,
            Analysis = PriceAnalyzer.Analyze(parsed, search.Total, rates, query.Item.StackSize),
        };
    }
}
