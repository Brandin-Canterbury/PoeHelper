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

public sealed class CompareViewModel : Observable
{
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

    public void Show(ParsedItem item, IReadOnlyList<SlotComparison> comparisons, CharacterBuild build)
    {
        Item = item;
        Title = item.DisplayName;
        TitleBrush = PriceViewModel.RarityBrush(item.Rarity);
        var age = DateTime.UtcNow - build.LoadedAt;
        Subtitle = $"{build.Name} · L{build.Level} {build.ClassName} · gear loaded {(age.TotalMinutes < 1 ? "just now" : $"{age.TotalMinutes:0} min ago")}";
        Error = null;
        Busy = false;
        Tabs.Clear();
        foreach (var c in comparisons) Tabs.Add(new SlotTab(c));
        Raise(nameof(HasTabs));
        Raise(nameof(ResultVisible));
        // Rings: start on the slot where the new item helps most.
        var best = comparisons.OrderByDescending(c => c.Overall == Sentiment.Bad ? -1 : (int)(c.Overall == Sentiment.Better ? 2 : c.Overall == Sentiment.Neutral ? 1 : 0))
                              .ThenByDescending(c => c.After.Life + c.After.RfDamageIndex / 100).FirstOrDefault();
        Select(Tabs.FirstOrDefault(t => t.Comparison == best));
    }

    public void Select(SlotTab? tab)
    {
        if (tab is null) return;
        foreach (var t in Tabs) t.Selected = t == tab;
        var c = tab.Comparison;
        Selected = c;
        CurrentItem = c.Current is null ? $"Your {c.SlotLabel}: (empty)" : $"Your {c.SlotLabel}: {c.Current.Name}";
        Verdict = c.Verdict;
        VerdictBrush = c.Overall switch
        {
            Sentiment.Better => CompareRowView.SentimentBrush(Sentiment.Better),
            Sentiment.Bad => CompareRowView.SentimentBrush(Sentiment.Bad),
            Sentiment.Worse => CompareRowView.SentimentBrush(Sentiment.Worse),
            _ => new SolidColorBrush(Color.FromRgb(0xf2, 0xc8, 0x6a)),
        };
        Rows.Clear();
        foreach (var r in c.Rows) Rows.Add(new CompareRowView(r));
        Warnings.Clear();
        foreach (var w in c.Warnings) Warnings.Add(w);
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
