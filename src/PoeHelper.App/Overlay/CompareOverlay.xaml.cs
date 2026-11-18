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
}
