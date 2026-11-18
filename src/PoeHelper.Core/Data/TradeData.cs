using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PoeHelper.Core.Items;
using PoeHelper.Core.Trade;

namespace PoeHelper.Core.Data;

public sealed record StatEntry(string Id, string Text, string Type);

public sealed record ItemEntry(string Type, string? Name, string? Text, string? Disc, bool Unique, string Category);

/// <summary>
/// The trade site's static data (stats, items, static exchange slugs), cached on disk for a day.
/// </summary>
public sealed partial class TradeData
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    readonly Dictionary<string, Dictionary<string, List<StatEntry>>> _statsByType = new(); // type → skeleton → entries
    readonly Dictionary<string, StatEntry> _statsById = new();
    readonly List<ItemEntry> _items = [];
    readonly HashSet<string> _baseTypes = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _exchangeIdByText = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _exchangeTextById = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ItemEntry> Items => _items;

    public static async Task<TradeData> LoadAsync(TradeClient client, string cacheDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(cacheDir);
        var stats = await LoadOne(client, cacheDir, "stats", ct);
        var items = await LoadOne(client, cacheDir, "items", ct);
        var stat = await LoadOne(client, cacheDir, "static", ct);
        return FromJson(stats, items, stat);
    }

    static async Task<JsonObject> LoadOne(TradeClient client, string dir, string name, CancellationToken ct)
    {
        var path = Path.Combine(dir, $"{name}.json");
        if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < MaxAge)
            return JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!.AsObject();
        try
        {
            var json = await client.GetDataAsync(name, ct);
            await File.WriteAllTextAsync(path, json.ToJsonString(), ct);
            return json;
        }
        catch when (File.Exists(path))
        {
            return JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!.AsObject(); // stale beats nothing
        }
    }

    public static TradeData FromJson(JsonObject stats, JsonObject items, JsonObject stat)
    {
        var d = new TradeData();
        foreach (var group in stats["result"]!.AsArray())
        {
            var type = group!["id"]!.GetValue<string>();
            var byText = d._statsByType[type] = new Dictionary<string, List<StatEntry>>();
            foreach (var e in group["entries"]!.AsArray())
            {
                var entry = new StatEntry(e!["id"]!.GetValue<string>(), e["text"]!.GetValue<string>(), type);
                d._statsById[entry.Id] = entry;
                var key = Skeleton(entry.Text);
                if (!byText.TryGetValue(key, out var list)) byText[key] = list = [];
                list.Add(entry);
            }
        }
        foreach (var group in items["result"]!.AsArray())
        {
            var cat = group!["id"]!.GetValue<string>();
            foreach (var e in group["entries"]!.AsArray())
            {
                var ie = new ItemEntry(
                    e!["type"]!.GetValue<string>(), e["name"]?.GetValue<string>(), e["text"]?.GetValue<string>(),
                    e["disc"]?.GetValue<string>(), e["flags"]?["unique"]?.GetValue<bool>() == true, cat);
                d._items.Add(ie);
                if (!ie.Unique && ie.Disc is null) d._baseTypes.Add(ie.Type);
            }
        }
        foreach (var group in stat["result"]!.AsArray())
            foreach (var e in group!["entries"]!.AsArray())
            {
                var id = e!["id"]?.GetValue<string>();
                var text = e["text"]?.GetValue<string>();
                if (id is null || text is null || id == "sep") continue;
                d._exchangeIdByText.TryAdd(text, id);
                d._exchangeTextById.TryAdd(id, text);
            }
        return d;
    }

    /// <summary>Numbers → '#', so "+40% to Fire Resistance" and "+#% to Fire Resistance" share a key.</summary>
    public static string Skeleton(string text) => NumberRx().Replace(text, "#");

    public StatEntry? StatById(string id) => _statsById.GetValueOrDefault(id);

    public IReadOnlyList<StatEntry> StatsBySkeleton(string type, string skeleton) =>
        _statsByType.TryGetValue(type, out var map) && map.TryGetValue(skeleton, out var list) ? list : [];

    /// <summary>Exchange slug (e.g. "divine") for an item name, if it trades in bulk.</summary>
    public string? ExchangeId(string name) => _exchangeIdByText.GetValueOrDefault(name);
    public string? ExchangeText(string id) => _exchangeTextById.GetValueOrDefault(id);

    public bool IsBaseType(string s) => _baseTypes.Contains(s);

    /// <summary>Finds the base type inside a magic item's name ("Hale Astral Plate of the Whale" → "Astral Plate").</summary>
    public string ResolveBaseType(string typeLine)
    {
        if (_baseTypes.Contains(typeLine)) return typeLine;
        string? best = null;
        foreach (var b in _baseTypes)
            if (b.Length > (best?.Length ?? 0) && ContainsWord(typeLine, b)) best = b;
        return best ?? typeLine;
    }

    /// <summary>Transfigured gem lookup: "Righteous Fire of Arcane Devotion" → (type "Righteous Fire", disc "alt_x").</summary>
    public ItemEntry? FindGem(string name) =>
        _items.FirstOrDefault(i => i.Category == "gem" && i.Disc is not null && i.Text == name);

    public ItemEntry? FindUnique(string name, string baseType) =>
        _items.FirstOrDefault(i => i.Unique && i.Name == name && i.Type == baseType)
        ?? _items.FirstOrDefault(i => i.Unique && i.Name == name);

    static bool ContainsWord(string haystack, string needle)
    {
        int i = haystack.IndexOf(needle, StringComparison.Ordinal);
        if (i < 0) return false;
        bool startOk = i == 0 || haystack[i - 1] == ' ';
        int end = i + needle.Length;
        bool endOk = end == haystack.Length || haystack[end] == ' ';
        return startOk && endOk;
    }

    [GeneratedRegex(@"\d+(?:\.\d+)?")]
    private static partial Regex NumberRx();
}
