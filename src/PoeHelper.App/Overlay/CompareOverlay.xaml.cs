using System.Windows;
using System.Windows.Input;

namespace PoeHelper.App.Overlay;

public partial class CompareOverlay : Window
{
    public CompareViewModel Model { get; } = new();
    public event Action? FindUpgradeRequested;
    public event Action? ReloadRequested;

    public CompareOverlay()
    {
        InitializeComponent();
        DataContext = Model;
    }

    public void ShowNearCursor(nint gameWindow)
    {
        if (!IsVisible) Show();
        OverlayPlacement.PlaceBesideCursor(this, gameWindow);
    }

    void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    void OnClose(object sender, RoutedEventArgs e) => Hide();
    void OnTab(object sender, RoutedEventArgs e) => Model.Select(((FrameworkElement)sender).DataContext as SlotTab);
    void OnFindUpgrade(object sender, RoutedEventArgs e) => FindUpgradeRequested?.Invoke();
    void OnReload(object sender, RoutedEventArgs e) => ReloadRequested?.Invoke();

    void OnOpenUpgrades(object sender, RoutedEventArgs e)
    {
        if (Model.UpgradeUrl is { } url) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    void OnOpenListing(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is UpgradeRow { Url: { } url })
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    void OnCopyWhisper(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not UpgradeRow { Whisper: { } w } || sender is not System.Windows.Controls.Button b) return;
        try { Clipboard.SetText(w); b.Content = "Copied ✓"; }
        catch (Exception ex) { Log.Info("Copy whisper failed: " + ex.Message); }
    }

    void OnUpgradeRowClick(object sender, MouseButtonEventArgs e)
    {
        // Clicks on the Trade / Whisper buttons are theirs, not the row's.
        if (e.OriginalSource is DependencyObject d && FindButton(d)) return;
        if (((FrameworkElement)sender).DataContext is UpgradeRow row) Model.ShowUpgradeDetail(row);
    }

    static bool FindButton(DependencyObject? d)
    {
        for (; d is not null and not System.Windows.Controls.Border { Name: "row" }; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            if (d is System.Windows.Controls.Primitives.ButtonBase) return true;
        return false;
    }

    void OnBackFromDetail(object sender, RoutedEventArgs e) => Model.BackFromDetail();
}
