using System.Net.Http.Headers;

namespace PoeHelper.Core.Trade;

public sealed class RateLimitedException(TimeSpan wait, string policy)
    : Exception($"Trade API rate limit ({policy}): try again in {Math.Ceiling(wait.TotalSeconds)} s.")
{
    public TimeSpan Wait { get; } = wait;
}

/// <summary>
/// Client-side mirror of GGG's X-Rate-Limit-* headers. Each endpoint family ("search", "fetch", "exchange",
/// "character") learns its policy rules from the first response, then requests are held back so we never
/// reach a rule's limit (one request of headroom is kept). Waits longer than <see cref="MaxWait"/> throw
/// instead, so a hotkey press reports "try again in N s" rather than hanging.
/// </summary>
public sealed class RateLimiter
{
    sealed record Rule(int Hits, int PeriodSeconds, int PenaltySeconds);

    sealed class Policy
    {
        public List<Rule> Rules = [];
        public readonly List<DateTime> History = [];
        public DateTime BlockedUntil = DateTime.MinValue;
        public readonly SemaphoreSlim Gate = new(1, 1);
    }

    readonly Dictionary<string, Policy> _policies = new();
    readonly object _lock = new();
    public TimeSpan MaxWait { get; set; } = TimeSpan.FromSeconds(12);
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    Policy Get(string key)
    {
        lock (_lock)
        {
            if (!_policies.TryGetValue(key, out var p)) _policies[key] = p = new Policy();
            return p;
        }
    }

    /// <summary>Waits until a request on <paramref name="key"/> is allowed, then records it.</summary>
    public async Task<IDisposable> AcquireAsync(string key, CancellationToken ct = default)
    {
        var p = Get(key);
        await p.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = TimeUntilAllowed(p);
            if (wait > MaxWait) throw new RateLimitedException(wait, key);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            lock (p) p.History.Add(Clock());
            return new Releaser(p.Gate);
        }
        catch
        {
            p.Gate.Release();
            throw;
        }
    }

    /// <summary>How long a request on this key would currently have to wait (for UI hints).</summary>
    public TimeSpan Peek(string key) => TimeUntilAllowed(Get(key));

    TimeSpan TimeUntilAllowed(Policy p)
    {
        lock (p)
        {
            var now = Clock();
            var wait = p.BlockedUntil > now ? p.BlockedUntil - now : TimeSpan.Zero;
            foreach (var r in p.Rules)
            {
                var window = TimeSpan.FromSeconds(r.PeriodSeconds);
                var inWindow = p.History.Where(t => now - t < window).OrderBy(t => t).ToList();
                int allowed = Math.Max(1, r.Hits - 1); // keep one request of headroom
                if (inWindow.Count < allowed) continue;
                var until = inWindow[inWindow.Count - allowed] + window - now;
                if (until > wait) wait = until;
            }
            // Drop history older than the longest window.
            int longest = p.Rules.Count == 0 ? 60 : p.Rules.Max(r => r.PeriodSeconds);
            p.History.RemoveAll(t => now - t > TimeSpan.FromSeconds(longest));
            return wait;
        }
    }

    /// <summary>Feeds a response's headers back so rules and server-side state stay in sync.</summary>
    public void Update(string key, HttpResponseHeaders headers, int statusCode)
    {
        var p = Get(key);
        lock (p)
        {
            var now = Clock();
            if (headers.TryGetValues("X-Rate-Limit-Rules", out var ruleNames))
            {
                var rules = new List<Rule>();
                var states = new List<Rule>();
                foreach (var name in ruleNames.SelectMany(v => v.Split(',')).Select(n => n.Trim()))
                {
                    if (headers.TryGetValues($"X-Rate-Limit-{name}", out var def)) rules.AddRange(ParseRules(def.First()));
                    if (headers.TryGetValues($"X-Rate-Limit-{name}-State", out var st)) states.AddRange(ParseRules(st.First()));
                }
                if (rules.Count > 0) p.Rules = rules;

                // State is "current hits:period:active penalty". Trust the server if it counts more than we do
                // (another tool on the same IP), and honour an active penalty.
                foreach (var s in states)
                {
                    if (s.PenaltySeconds > 0)
                        p.BlockedUntil = Max(p.BlockedUntil, now.AddSeconds(s.PenaltySeconds));
                    int ours = p.History.Count(t => now - t < TimeSpan.FromSeconds(s.PeriodSeconds));
                    for (int i = ours; i < s.Hits; i++) p.History.Add(now);
                }
            }
            if (statusCode == 429)
            {
                int retry = 60;
                if (headers.RetryAfter?.Delta is { } d) retry = (int)d.TotalSeconds;
                else if (headers.TryGetValues("Retry-After", out var ra) && int.TryParse(ra.First(), out var s)) retry = s;
                p.BlockedUntil = Max(p.BlockedUntil, now.AddSeconds(retry));
            }
        }
    }

    static IEnumerable<Rule> ParseRules(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split(':'))
            .Where(a => a.Length == 3)
            .Select(a => new Rule(int.Parse(a[0]), int.Parse(a[1]), int.Parse(a[2])));

    static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) gate.Release(); }
    }
}
