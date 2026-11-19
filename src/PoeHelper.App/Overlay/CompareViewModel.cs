using System.Collections.ObjectModel;
using System.Windows.Media;
using PoeHelper.Core.Build;
using PoeHelper.Core.Items;

namespace PoeHelper.App.Overlay;

public sealed class CompareRowView(CompareRow row)
{
    public string Label => row.Label;
    public string Before => row.Before;
    public string After => row.After;
    public string Delta => row.Delta;
    public Brush DeltaBrush => SentimentBrush(row.Sentiment);
    public bool Arrow => row.Before.Length > 0 || row.After.Length > 0;

    public static Brush SentimentBrush(Sentiment s) => s switch
    {
        Sentiment.Better => new SolidColorBrush(Color.FromRgb(0x6c, 0xc0, 0x6c)),
        Sentiment.Worse => new SolidColorBrush(Color.FromRgb(0xd8, 0xa0, 0x5a)),
        Sentiment.Bad => new SolidColorBrush(Color.FromRgb(0xe0, 0x6a, 0x5a)),
        _ => new SolidColorBrush(Color.FromRgb(0x9a, 0x8f, 0x80)),
    };
}

public sealed class SlotTab(SlotComparison c) : Observable
{
    bool _selected;
    public SlotComparison Comparison { get; } = c;
    public string Label => $"vs {c.SlotLabel}";
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
}

public sealed class ModLine(string text)
{
    public string Text => text;
    /// <summary>Red for anything that takes away: negative values and "reduced".</summary>
    public Brush Brush => System.Text.RegularExpressions.Regex.IsMatch(text, @"(^|\s)-\d| reduced |^Cannot |^Lose |against you|^You take ")
        ? CompareRowView.SentimentBrush(Sentiment.Bad) : TextBrush;
    static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0xe8, 0xe0, 0xd2));
}

public sealed class UpgradeRow : Observable
{
    bool _selected;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public required PoeHelper.Core.Build.UpgradeCandidate Candidate { get; init; }
    public required string Price { get; init; }
    public required string Name { get; init; }
    public required string Gains { get; init; }
    public required Brush GainsBrush { get; init; }
    public required string Age { get; init; }
    public string? Url { get; init; }
    public string? Whisper { get; init; }
    public bool HasUrl => Url is not null;
    public bool HasWhisper => Whisper is not null;
}

public sealed class CompareViewModel : Observable
{
    // ---- find upgrade
    string _budget = "1";
    bool _budgetInDivines = true;
    bool _includeChaos;
    bool _upgradeBusy;
    string _upgradeStatus = "";
    string? _upgradeUrl;
    public string Budget { get => _budget; set => Set(ref _budget, value); }
    public bool BudgetInDivines { get => _budgetInDivines; set { Set(ref _budgetInDivines, value); Raise(nameof(BudgetInChaos)); } }
    public bool BudgetInChaos { get => !_budgetInDivines; set => BudgetInDivines = !value; }
    public bool IncludeChaos { get => _includeChaos; set => Set(ref _includeChaos, value); }
    public bool UpgradeBusy { get => _upgradeBusy; set => Set(ref _upgradeBusy, value); }
    public string UpgradeStatus { get => _upgradeStatus; set { Set(ref _upgradeStatus, value); Raise(nameof(HasUpgradeStatus)); } }
    public bool HasUpgradeStatus => UpgradeStatus.Length > 0;
    public string? UpgradeUrl { get => _upgradeUrl; set { Set(ref _upgradeUrl, value); Raise(nameof(HasUpgradeUrl)); } }
    public bool HasUpgradeUrl => UpgradeUrl is not null;
    public ObservableCollection<UpgradeRow> Upgrades { get; } = [];
    public ObservableCollection<string> UpgradeConstraints { get; } = [];
    public bool HasUpgrades => Upgrades.Count > 0;

    public void ClearUpgrades()
    {
        Upgrades.Clear();
        UpgradeConstraints.Clear();
        UpgradeStatus = "";
        UpgradeUrl = null;
        Raise(nameof(HasUpgrades));
    }

    public void ShowUpgrades(PoeHelper.Core.Build.UpgradeResult r)
    {
        Upgrades.Clear();
        UpgradeConstraints.Clear();
        foreach (var c in r.Plan.Constraints) UpgradeConstraints.Add(c);
        foreach (var c in r.Candidates.Take(10))
        {
            var cmp = c.Compare;
            var l = c.Listing;
            var age = DateTime.UtcNow - l.Indexed;
            Upgrades.Add(new UpgradeRow
            {
                Price = $"{l.Amount:0.##} {(l.Currency == "divine" ? "div" : l.Currency == "chaos" ? "c" : l.Currency)}",
                Name = cmp.Candidate?.Name ?? "?",
                Gains = c.Gains.Length > 0 ? c.Gains : "no change in tracked stats",
                GainsBrush = CompareRowView.SentimentBrush(cmp.Overall),
                Age = age.TotalDays >= 1 ? $"{age.TotalDays:0}d" : $"{Math.Max(0, age.TotalHours):0}h",
                Url = c.Url,
                Whisper = c.Whisper,
                Candidate = c,
            });
        }
        UpgradeUrl = r.SearchUrl;
        var opts = r.Plan.Options;
        string ranking = $"resists{(opts.IncludeChaos ? " (chaos included)" : "")}, then {string.Join(", ", opts.Tracked.Select(t => t.IsDamage ? t.LabelFor(r.Skill) : t.Label.ToLowerInvariant()))}";
        UpgradeStatus = r.Candidates.Count == 0
            ? (r.Total == 0 ? "Nothing listed meets the constraints within budget. Raise the budget or track other stats (Settings → Upgrade tracker)."
                            : $"{r.Total} listed, but none of the ones checked beat your current item without breaking something.")
            : $"Click a result to see everything it changes. Best {Math.Min(10, r.Candidates.Count)} of {(r.Total >= 10000 ? "10,000+" : r.Total.ToString("N0"))} listed, ranked by {ranking}. Resists that stay capped aren't shown"
              + (r.Rejected > 0 ? $" ({r.Rejected} rejected: they'd break resists, sockets or requirements, or gain nothing you track)" : "")
              + ". Trade ↗ opens that one listing; Whisper copies the buy message."
              + (r.Plan.Weighted ? " (Weighted search: logged in.)" : "")
              + (r.Note is null ? "" : " " + r.Note);
        Raise(nameof(HasUpgrades));
    }
    string _title = "";
    string _subtitle = "";
    Brush _titleBrush = Brushes.White;
    string _currentItem = "";
    string _verdict = "";
    Brush _verdictBrush = Brushes.White;
    string? _error;
    bool _busy;

    public string Title { get => _title; set => Set(ref _title, value); }
    public string Subtitle { get => _subtitle; set => Set(ref _subtitle, value); }
    string _damageNote = "";
    public string DamageNote { get => _damageNote; set => Set(ref _damageNote, value); }
    public Brush TitleBrush { get => _titleBrush; set => Set(ref _titleBrush, value); }
    public string CurrentItem { get => _currentItem; set => Set(ref _currentItem, value); }
    public string Verdict { get => _verdict; set => Set(ref _verdict, value); }
    public Brush VerdictBrush { get => _verdictBrush; set => Set(ref _verdictBrush, value); }
    public string? Error { get => _error; set { Set(ref _error, value); Raise(nameof(HasError)); Raise(nameof(ResultVisible)); } }
    public bool Busy { get => _busy; set { Set(ref _busy, value); Raise(nameof(ResultVisible)); } }
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool ResultVisible => !HasError && !Busy && Tabs.Count > 0;
    public bool HasTabs => Tabs.Count > 1;

    public ParsedItem? Item { get; private set; }
    public SlotComparison? Selected { get; private set; }
    public ObservableCollection<SlotTab> Tabs { get; } = [];
    public ObservableCollection<CompareRowView> Rows { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public ObservableCollection<ModLine> DetailMods { get; } = [];
    public ObservableCollection<ModLine> DetailLoses { get; } = [];

    // ---- upgrade detail: the stat rows show "if you buy this" instead of the hovered item
    UpgradeRow? _detail;
    public UpgradeRow? Detail { get => _detail; private set { Set(ref _detail, value); Raise(nameof(InDetail)); Raise(nameof(BackLabel)); } }
    public bool InDetail => Detail is not null;
    public string BackLabel => $"← Back to {Item?.DisplayName ?? "hovered item"}";
    public string LosesHeader { get; private set; } = "";

    public void ShowUpgradeDetail(UpgradeRow row)
    {
        foreach (var u in Upgrades) u.Selected = u == row;
        var c = row.Candidate.Compare;
        Detail = row;
        ShowComparison(c, $"If you buy {c.Candidate?.Name} for {row.Price} and wear it as your {c.SlotLabel}"
                          + (c.Current is null ? ":" : $", replacing {c.Current.Name}:"), row.Candidate.Rows, row.Candidate.Warnings);
        DetailMods.Clear();
        DetailLoses.Clear();
        foreach (var m in c.Candidate?.Mods ?? []) DetailMods.Add(new ModLine(m));
        foreach (var m in c.Current?.Mods ?? []) DetailLoses.Add(new ModLine(m));
        LosesHeader = c.Current is null ? "" : $"YOU LOSE ({c.Current.Name})";
        Raise(nameof(LosesHeader));
    }

    public void BackFromDetail()
    {
        foreach (var u in Upgrades) u.Selected = false;
        Detail = null;
        DetailMods.Clear();
        DetailLoses.Clear();
        if (Selected is { } s) ShowComparison(s, s.Current is null ? $"Your {s.SlotLabel}: (empty)" : $"Your {s.SlotLabel}: {s.Current.Name}");
    }

    public void Show(ParsedItem item, IReadOnlyList<SlotComparison> comparisons, CharacterBuild build)
    {
        Item = item;
        Title = item.DisplayName;
        TitleBrush = PriceViewModel.RarityBrush(item.Rarity);
        var age = DateTime.UtcNow - build.LoadedAt;
        Subtitle = $"{build.Name} · L{build.Level} {build.ClassName} · gear loaded {(age.TotalMinutes < 1 ? "just now" : $"{age.TotalMinutes:0} min ago")}";
        var sk = build.Skill;
        DamageNote = sk is null
            ? "No active skill gem found, so damage only counts generic \"increased Damage\"."
            : $"Damage is scored for {sk.Describe()}{(sk.Auto ? ", detected from your links (pick another in Settings)" : ", picked in Settings")}. "
              + (!sk.IsMinion && PoeHelper.Core.Data.SkillData.Instance.Get(sk.Name) is { Support: false }
                  ? "Uses Path of Building's gem data: base damage per level, cast speed, crit, and your linked supports' multipliers. "
                  : sk.IsMinion ? "Minions: minion damage, speed and gem levels (generic model). "
                  : "Not in Path of Building's data, so a generic model: increased × multipliers × speed × gem levels. ")
              + (SkillFormulas.For(sk) is { } f ? f.Summary + " " : "")
              + "Relative: no enemy resists, conversion or conditional mods.";
        Error = null;
        Busy = false;
        Tabs.Clear();
        foreach (var c in comparisons) Tabs.Add(new SlotTab(c));
        Raise(nameof(HasTabs));
        Raise(nameof(ResultVisible));
        // Rings: start on the slot where the new item helps most.
        var best = comparisons.OrderByDescending(c => c.Overall == Sentiment.Bad ? -1 : (int)(c.Overall == Sentiment.Better ? 2 : c.Overall == Sentiment.Neutral ? 1 : 0))
                              .ThenByDescending(c => c.Score).FirstOrDefault();
        Select(Tabs.FirstOrDefault(t => t.Comparison == best));
    }

    public void Select(SlotTab? tab)
    {
        if (tab is null) return;
        foreach (var t in Tabs) t.Selected = t == tab;
        var c = tab.Comparison;
        Selected = c;
        Detail = null;
        DetailMods.Clear();
        DetailLoses.Clear();
        ShowComparison(c, c.Current is null ? $"Your {c.SlotLabel}: (empty)" : $"Your {c.SlotLabel}: {c.Current.Name}");
        ClearUpgrades();
    }

    void ShowComparison(SlotComparison c, string header, IEnumerable<CompareRow>? rows = null, IEnumerable<string>? warnings = null)
    {
        CurrentItem = header;
        Verdict = c.Verdict;
        VerdictBrush = c.Overall switch
        {
            Sentiment.Better => CompareRowView.SentimentBrush(Sentiment.Better),
            Sentiment.Bad => CompareRowView.SentimentBrush(Sentiment.Bad),
            Sentiment.Worse => CompareRowView.SentimentBrush(Sentiment.Worse),
            _ => new SolidColorBrush(Color.FromRgb(0xf2, 0xc8, 0x6a)),
        };
        Rows.Clear();
        foreach (var r in rows ?? c.Rows) Rows.Add(new CompareRowView(r));
        Warnings.Clear();
        foreach (var w in warnings ?? c.Warnings) Warnings.Add(w);
    }

    public void ShowMessage(string title, string message)
    {
        Title = title;
        TitleBrush = Brushes.White;
        Subtitle = "";
        Tabs.Clear();
        Rows.Clear();
        Warnings.Clear();
        Busy = false;
        Error = message;
        Raise(nameof(HasTabs));
    }
}
