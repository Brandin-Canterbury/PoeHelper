using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using PoeHelper.Core.Items;
using PoeHelper.Core.Pricing;

namespace PoeHelper.App.Overlay;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class FilterRow(FilterChoice choice) : Observable
{
    public FilterChoice Choice { get; } = choice;
    public string Label => Choice.Label;
    public string Range => Choice.Key.StartsWith("pseudo.pseudo_has_") ? ""
        : Choice.Min is { } mn ? $"≥ {mn:0.##}" : Choice.Max is { } mx ? $"≤ {mx:0.##}" : "";
    public bool Enabled
    {
        get => Choice.Enabled;
        set { Choice.Enabled = value; Raise(nameof(Enabled)); }
    }
}

public sealed class ListingRow
{
    public required string Price { get; init; }
    public required string Equivalent { get; init; }
    public required string Summary { get; init; }
    public required string Age { get; init; }
    public required string Seller { get; init; }
    public bool Outlier { get; init; }
    public double Opacity => Outlier ? 0.45 : 1;
}

public sealed class PriceViewModel : Observable
{
    string _title = "";
    string _subtitle = "";
    Brush _titleBrush = Brushes.White;
    string _headline = "";
    string _status = "";
    string _confidence = "";
    Brush _confidenceBrush = Brushes.Gray;
    bool _busy;
    string? _error;
    string? _tradeUrl;

    public string Title { get => _title; set => Set(ref _title, value); }
    public string Subtitle { get => _subtitle; set => Set(ref _subtitle, value); }
    public Brush TitleBrush { get => _titleBrush; set => Set(ref _titleBrush, value); }
    public string Headline { get => _headline; set => Set(ref _headline, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Confidence { get => _confidence; set => Set(ref _confidence, value); }
    public Brush ConfidenceBrush { get => _confidenceBrush; set => Set(ref _confidenceBrush, value); }
    public bool Busy { get => _busy; set { Set(ref _busy, value); Raise(nameof(ResultVisible)); } }
    public string? Error { get => _error; set { Set(ref _error, value); Raise(nameof(HasError)); Raise(nameof(ResultVisible)); } }
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool ResultVisible => !Busy && !HasError;
    public string? TradeUrl { get => _tradeUrl; set => Set(ref _tradeUrl, value); }
    public bool HasFilters => Filters.Count > 0;

    public ObservableCollection<string> Notes { get; } = [];
    public ObservableCollection<FilterRow> Filters { get; } = [];
    public ObservableCollection<ListingRow> Listings { get; } = [];
    public PriceQuery? Query { get; private set; }

    public void BeginQuery(PriceQuery query)
    {
        Query = query;
        var item = query.Item;
        Title = item.DisplayName;
        TitleBrush = RarityBrush(item.Rarity);
        Subtitle = Describe(item, query);
        Filters.Clear();
        foreach (var f in query.Filters) Filters.Add(new FilterRow(f));
        Raise(nameof(HasFilters));
        Error = null;
        Busy = true;
        Headline = "Searching…";
        Notes.Clear();
        Listings.Clear();
    }

    public void ShowResult(PriceCheckResult r)
    {
        var a = r.Analysis;
        Busy = false;
        Headline = a.Headline;
        TradeUrl = r.TradeUrl;
        Status = $"{a.TotalListed} listed · {(r.Query.Kind == QueryKind.Exchange ? "bulk exchange" : StatusText(r.Query.Status))} · 1 div = {a.DivineRate:0}c";
        (Confidence, ConfidenceBrush) = a.Confidence switch
        {
            Core.Pricing.Confidence.High => ("High confidence", (Brush)new SolidColorBrush(Color.FromRgb(0x6c, 0xc0, 0x6c))),
            Core.Pricing.Confidence.Medium => ("Medium confidence", new SolidColorBrush(Color.FromRgb(0xd8, 0xb4, 0x4a))),
            Core.Pricing.Confidence.Low => ("Low confidence", new SolidColorBrush(Color.FromRgb(0xd8, 0x7a, 0x4a))),
            _ => ("No data", Brushes.Gray),
        };
        Notes.Clear();
        foreach (var n in a.Notes) Notes.Add(n);
        Listings.Clear();
        foreach (var l in a.Listings.Take(20))
        {
            Listings.Add(new ListingRow
            {
                Price = $"{l.Amount:0.##} {Short(l.Currency)}",
                Equivalent = l.Chaos is { } c && l.Currency != "chaos" ? $"≈ {c:0}c" : l.Chaos is { } c2 && c2 >= a.DivineRate ? $"≈ {c2 / a.DivineRate:0.#} div" : "",
                Summary = (l.Outlier ? "bait? " : "") + l.Summary,
                Age = Age(l.Indexed),
                Seller = l.Account,
                Outlier = l.Outlier,
            });
        }
    }

    public void ShowError(string message)
    {
        Busy = false;
        Error = message;
    }

    static string Describe(ParsedItem item, PriceQuery q)
    {
        var parts = new List<string>();
        if (item.Rarity == ItemRarity.Gem) parts.Add($"Level {item.GemLevel}, {item.Quality}% quality");
        else if (item.Rarity is ItemRarity.Rare or ItemRarity.Unique) parts.Add(item.ItemClass.TrimEnd('s'));
        if (item.MaxLinks >= 5) parts.Add($"{item.MaxLinks}-link");
        if (item.ItemLevel > 0 && item.Rarity is not ItemRarity.Unique) parts.Add($"ilvl {item.ItemLevel}");
        if (item.StackSize > 1) parts.Add($"stack of {item.StackSize}");
        if (item.Corrupted) parts.Add("corrupted");
        if (q.Kind == QueryKind.Search && item.Rarity is ItemRarity.Rare && !item.AdvancedText)
            parts.Add("(no mod tiers: advanced copy off)");
        return string.Join(" · ", parts);
    }

    static string StatusText(string status) => status switch
    {
        "securable" => "Instant Buyout",
        "available" => "Instant Buyout + in person",
        _ => "online",
    };

    static string Short(string currency) => currency switch { "chaos" => "c", "divine" => "div", _ => currency };

    static string Age(DateTime indexed)
    {
        var age = DateTime.UtcNow - indexed;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        return age.TotalHours < 1 ? $"{age.TotalMinutes:0}m" : age.TotalDays < 1 ? $"{age.TotalHours:0}h" : $"{age.TotalDays:0}d";
    }

    public static Brush RarityBrush(ItemRarity r) => new SolidColorBrush(r switch
    {
        ItemRarity.Unique => Color.FromRgb(0xaf, 0x60, 0x25),
        ItemRarity.Rare => Color.FromRgb(0xff, 0xff, 0x77),
        ItemRarity.Magic => Color.FromRgb(0x88, 0x88, 0xff),
        ItemRarity.Gem => Color.FromRgb(0x1b, 0xa2, 0x9b),
        ItemRarity.Currency => Color.FromRgb(0xaa, 0x9e, 0x82),
        ItemRarity.DivinationCard => Color.FromRgb(0x0e, 0xba, 0xff),
        _ => Color.FromRgb(0xc8, 0xc8, 0xc8),
    });
}
