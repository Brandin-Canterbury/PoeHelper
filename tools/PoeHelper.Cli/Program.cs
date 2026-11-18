// Dev/QA runner: price-checks item text files against the live league without the overlay.
//   dotnet run --project tools/PoeHelper.Cli -- price <item.txt> [more.txt ...]
using PoeHelper.Core;
using PoeHelper.Core.Data;
using PoeHelper.Core.Pricing;
using PoeHelper.Core.Trade;

var settings = AppSettings.Load();
using var client = new TradeClient(TradeClient.BuildUserAgent(settings.Contact));
client.Limiter.MaxWait = TimeSpan.FromSeconds(90); // the CLI may wait; the overlay fails fast instead

if (args.Length < 2 || args[0] != "price")
{
    Console.WriteLine("usage: price <item.txt> [...]");
    return 1;
}

var data = await TradeData.LoadAsync(client, AppSettings.CacheDir);
var rates = new CurrencyRates();
var service = new PriceCheckService(client, data, rates);

foreach (var file in args.Skip(1))
{
    Console.WriteLine($"=== {Path.GetFileName(file)}");
    try
    {
        var query = service.BuildQuery(File.ReadAllText(file), settings.PriceCheckStatus);
        foreach (var f in query.Filters)
            Console.WriteLine($"  [{(f.Enabled ? 'x' : ' ')}] {f.Label}  min={f.Min} max={f.Max}  ({f.Key})");
        var r = await service.RunAsync(query, settings.League);
        var a = r.Analysis;
        Console.WriteLine($"  divine = {rates.ChaosPerDivine}c{(rates.IsFallback ? " (fallback)" : "")}");
        Console.WriteLine($"  {a.Headline}   [{a.Confidence}, {a.TotalListed} listed]");
        foreach (var n in a.Notes) Console.WriteLine($"   - {n}");
        foreach (var l in a.Listings.Take(8))
            Console.WriteLine($"     {(l.Outlier ? "BAIT " : "")}{l.Amount} {l.Currency} ({(l.Chaos is { } c ? a.Format(c) : "?")})  {l.Summary}  {(DateTime.UtcNow - l.Indexed).TotalHours:0}h  {l.Account}");
        Console.WriteLine($"  {r.TradeUrl}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ERROR {ex.GetType().Name}: {ex.Message}");
    }
}
return 0;
