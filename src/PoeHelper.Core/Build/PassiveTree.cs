using System.Text.Json.Nodes;

namespace PoeHelper.Core.Build;

/// <summary>
/// GGG's official passive tree export (github.com/grindinggear/skilltree-export), reduced to node stats.
/// Cached on disk for a week; the character API only returns allocated node IDs.
/// </summary>
public sealed class PassiveTree
{
    public const string Url = "https://raw.githubusercontent.com/grindinggear/skilltree-export/master/data.json";
    static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    readonly Dictionary<int, (string Name, List<string> Stats, bool Notable)> _nodes = new();
    readonly Dictionary<int, Dictionary<int, List<string>>> _mastery = new();
    readonly List<(string Name, int Str, int Dex, int Int)> _classes = new();

    public static async Task<PassiveTree> LoadAsync(string cacheDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(cacheDir);
        var path = Path.Combine(cacheDir, "passive-tree.json");
        string text;
        if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < MaxAge)
            text = await File.ReadAllTextAsync(path, ct);
        else
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd(TradeUserAgent);
                text = await http.GetStringAsync(Url, ct);
                await File.WriteAllTextAsync(path, text, ct);
            }
            catch when (File.Exists(path))
            {
                text = await File.ReadAllTextAsync(path, ct);
            }
        }
        return FromJson(JsonNode.Parse(text)!.AsObject());
    }

    const string TradeUserAgent = "PoeHelper/0.1 (+https://github.com/Brandin-Canterbury/PoeHelper)";

    public static PassiveTree FromJson(JsonObject json)
    {
        var t = new PassiveTree();
        foreach (var (id, node) in json["nodes"]!.AsObject())
        {
            if (!int.TryParse(id, out var hash) || node is null) continue;
            var stats = node["stats"]?.AsArray().Select(s => s!.GetValue<string>()).ToList() ?? [];
            t._nodes[hash] = (node["name"]?.GetValue<string>() ?? "", stats, node["isNotable"]?.GetValue<bool>() == true);
            if (node["masteryEffects"] is JsonArray effects)
                t._mastery[hash] = effects.ToDictionary(
                    e => e!["effect"]!.GetValue<int>(),
                    e => e!["stats"]!.AsArray().Select(s => s!.GetValue<string>()).ToList());
        }
        foreach (var c in json["classes"]!.AsArray())
            t._classes.Add((c!["name"]!.GetValue<string>(), c["base_str"]!.GetValue<int>(), c["base_dex"]!.GetValue<int>(), c["base_int"]!.GetValue<int>()));
        return t;
    }

    public IEnumerable<string> NodeStats(int hash) =>
        _nodes.TryGetValue(hash, out var n) ? n.Stats.SelectMany(s => s.Split('\n')) : [];

    public IEnumerable<string> MasteryStats(int node, int effect) =>
        _mastery.TryGetValue(node, out var m) && m.TryGetValue(effect, out var stats) ? stats.SelectMany(s => s.Split('\n')) : [];

    public IEnumerable<string> NotableStats(string name) =>
        _nodes.Values.FirstOrDefault(n => n.Notable && n.Name == name).Stats?.SelectMany(s => s.Split('\n')) ?? [];

    /// <summary>Base attributes by class index (the passives API's "character" field), falling back to the class name.</summary>
    public (int Str, int Dex, int Int, string Class) ClassAttributes(int classId, string className)
    {
        if (classId >= 0 && classId < _classes.Count) { var c = _classes[classId]; return (c.Str, c.Dex, c.Int, c.Name); }
        var byName = _classes.FirstOrDefault(c => c.Name == className);
        return byName.Name is null ? (20, 20, 20, "Scion") : (byName.Str, byName.Dex, byName.Int, byName.Name);
    }
}
