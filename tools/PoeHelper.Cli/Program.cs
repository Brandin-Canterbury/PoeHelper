// Dev/QA runner: price-checks item text files against the live league without the overlay.
//   dotnet run --project tools/PoeHelper.Cli -- price <item.txt> [more.txt ...]
using PoeHelper.Core;
using PoeHelper.Core.Build;
using PoeHelper.Core.Data;
using PoeHelper.Core.Pricing;
using PoeHelper.Core.Trade;

var settings = AppSettings.Load();
using var client = new TradeClient(TradeClient.BuildUserAgent(settings.Contact));
client.Limiter.MaxWait = TimeSpan.FromSeconds(90); // the CLI may wait; the overlay fails fast instead

if (args.Length == 3 && args[0] == "build")
{
    var build = await CharacterBuild.LoadAsync(client, args[1], args[2], AppSettings.CacheDir);
    var st = BuildCalculator.Compute(build);
    Console.WriteLine($"{build.Name} L{build.Level} {build.ClassName} ({build.League}) base {build.BaseStr}/{build.BaseDex}/{build.BaseInt}, tree lines {build.TreeStats.Count}, jewel lines {build.JewelMods.Count}");
    Console.WriteLine($"Life {st.Life}   Str {st.Str} Dex {st.Dex} Int {st.Int}");
    foreach (var (n, r) in new[] { ("Fire", st.Fire), ("Cold", st.Cold), ("Lightning", st.Lightning), ("Chaos", st.Chaos) })
        Console.WriteLine($"  {n,-9} {r.Effective}/{r.Max}  (total {r.Total}, overcap {r.Overcap})");
    foreach (var (n, a2) in new[] { ("Str", st.StrNeed), ("Dex", st.DexNeed), ("Int", st.IntNeed) })
        Console.WriteLine($"  {n} {a2.Have} vs need {a2.Need} ({a2.NeededBy}) {(a2.Met ? "ok" : "NOT MET")}");
    Console.WriteLine($"Regen {st.LifeRegen:0.0}/s  RF degen {st.RfDegen:0.0}/s  net {st.NetRegen:0.0}/s");
    Console.WriteLine($"Inc dmg {st.IncreasedDamage}%  DoT multi {st.FireDotMulti}%  RF index {st.RfDamageIndex:0}");
    foreach (var it in build.Items.Values) Console.WriteLine($"  [{it.Slot}] {it.Name}  sockets {it.SocketCount} links {it.MaxLinks} gems {it.Gems.Count}");
    return 0;
}

if (args.Length >= 4 && args[0] == "compare")
{
    var build = await CharacterBuild.LoadAsync(client, args[1], args[2], AppSettings.CacheDir);
    foreach (var file in args.Skip(3))
    {
        var item = PoeHelper.Core.Items.ItemParser.Parse(File.ReadAllText(file));
        Console.WriteLine($"=== {item.DisplayName}");
        foreach (var c in GearCompare.Compare(build, item))
        {
            Console.WriteLine($"  vs {c.SlotLabel}: {c.Current?.Name}");
            Console.WriteLine($"  {c.Overall}: {c.Verdict}");
            foreach (var w in c.Warnings) Console.WriteLine($"   ! {w}");
            foreach (var r in c.Rows) Console.WriteLine($"     {r.Label,-22} {r.Before,10} -> {r.After,-10} {r.Delta,8}  {r.Sentiment}");
        }
    }
    return 0;
}

if (args.Length < 2 || args[0] != "price")
{
    Console.WriteLine("usage: price <item.txt> [...] | build <account#1234> <character>");
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
