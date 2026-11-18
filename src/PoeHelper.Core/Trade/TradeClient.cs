using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PoeHelper.Core.Trade;

public sealed class TradeApiException(string message, HttpStatusCode status) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

public sealed record SearchResult(string Id, int Total, IReadOnlyList<string> Ids, JsonObject Query);

/// <summary>
/// Thin client over pathofexile.com's trade and character-window endpoints. Every call goes through the
/// <see cref="RateLimiter"/>. One hotkey press makes at most one search plus its fetches.
/// </summary>
public sealed class TradeClient : IDisposable
{
    public const string BaseUrl = "https://www.pathofexile.com";
    readonly HttpClient _http;
    public RateLimiter Limiter { get; } = new();

    public TradeClient(string userAgent, string? poeSessId = null, HttpMessageHandler? handler = null)
    {
        var h = handler ?? new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, UseCookies = false };
        _http = new HttpClient(h) { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        if (!string.IsNullOrWhiteSpace(poeSessId))
            _http.DefaultRequestHeaders.Add("Cookie", $"POESESSID={poeSessId}");
    }

    public static string BuildUserAgent(string? contact) =>
        string.IsNullOrWhiteSpace(contact)
            ? "PoeHelper/0.1 (+https://github.com/Brandin-Canterbury/PoeHelper)"
            : $"PoeHelper/0.1 (+https://github.com/Brandin-Canterbury/PoeHelper; contact: {contact.Trim()})";

    public async Task<SearchResult> SearchAsync(string league, JsonObject query, CancellationToken ct = default)
    {
        var json = await SendAsync("search", HttpMethod.Post, $"/api/trade/search/{Uri.EscapeDataString(league)}", query, ct);
        var ids = json["result"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
        return new SearchResult(json["id"]!.GetValue<string>(), json["total"]?.GetValue<int>() ?? ids.Count, ids, query);
    }

    /// <summary>Fetches listings 10 at a time (the API maximum).</summary>
    public async Task<List<JsonObject>> FetchAsync(SearchResult search, int count, CancellationToken ct = default)
    {
        var results = new List<JsonObject>();
        foreach (var chunk in search.Ids.Take(count).Chunk(10))
        {
            var json = await SendAsync("fetch", HttpMethod.Get,
                $"/api/trade/fetch/{string.Join(',', chunk)}?query={Uri.EscapeDataString(search.Id)}", null, ct);
            foreach (var r in json["result"]?.AsArray() ?? [])
                if (r is JsonObject o) results.Add(o);
        }
        return results;
    }

    public Task<JsonObject> ExchangeAsync(string league, JsonObject query, CancellationToken ct = default) =>
        SendAsync("exchange", HttpMethod.Post, $"/api/trade/exchange/{Uri.EscapeDataString(league)}", query, ct);

    public Task<JsonObject> GetDataAsync(string name, CancellationToken ct = default) =>
        SendAsync("data", HttpMethod.Get, $"/api/trade/data/{name}", null, ct);

    /// <summary>Public character endpoints (profile must be public). Returns the raw JSON (object or array).</summary>
    public async Task<JsonNode> CharacterWindowAsync(string endpoint, IDictionary<string, string> form, CancellationToken ct = default)
    {
        using var _ = await Limiter.AcquireAsync("character", ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/character-window/{endpoint}")
        { Content = new FormUrlEncodedContent(form) };
        using var resp = await _http.SendAsync(req, ct);
        Limiter.Update("character", resp.Headers, (int)resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new TradeApiException(resp.StatusCode == HttpStatusCode.Forbidden
                ? "Character data is private. Make the profile's Characters tab public on pathofexile.com."
                : $"Character API {(int)resp.StatusCode}: {Trim(body)}", resp.StatusCode);
        return JsonNode.Parse(body)!;
    }

    async Task<JsonObject> SendAsync(string key, HttpMethod method, string url, JsonObject? body, CancellationToken ct)
    {
        using var _ = await Limiter.AcquireAsync(key, ct);
        using var req = new HttpRequestMessage(method, url);
        if (body is not null)
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct);
        Limiter.Update(key, resp.Headers, (int)resp.StatusCode);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (resp.StatusCode == (HttpStatusCode)429)
            throw new RateLimitedException(Limiter.Peek(key), key);
        if (!resp.IsSuccessStatusCode)
        {
            string msg = Trim(text);
            try { msg = JsonNode.Parse(text)?["error"]?["message"]?.GetValue<string>() ?? msg; } catch (JsonException) { }
            throw new TradeApiException($"Trade API {(int)resp.StatusCode}: {msg}", resp.StatusCode);
        }
        return JsonNode.Parse(text)!.AsObject();
    }

    static string Trim(string s) => s.Length > 300 ? s[..300] : s;

    public void Dispose() => _http.Dispose();
}
