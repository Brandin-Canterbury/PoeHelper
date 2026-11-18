using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using PoeHelper.App.Native;

namespace PoeHelper.App.Overlay;

public sealed class InverseBoolToVisConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public partial class PriceOverlay : Window
{
    public PriceViewModel Model { get; } = new();
    public event Action? SearchAgainRequested;

    public PriceOverlay()
    {
        InitializeComponent();
        DataContext = Model;
    }

    /// <summary>Shows the panel beside the cursor, kept inside the game window, without stealing focus.</summary>
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

    void OnSearchAgain(object sender, RoutedEventArgs e) => SearchAgainRequested?.Invoke();

    void OnOpenTrade(object sender, RoutedEventArgs e)
    {
        if (Model.TradeUrl is { } url) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}

public static class OverlayPlacement
{
    /// <summary>Puts the window to the side of the cursor that has more room (items are often on the right).</summary>
    public static void PlaceBesideCursor(Window w, nint gameWindow)
    {
        Win32.GetCursorPos(out var cursor);
        Win32.RECT bounds;
        if (gameWindow == 0 || !Win32.GetWindowRect(gameWindow, out bounds))
        {
            var wa = SystemParameters.WorkArea;
            bounds = new Win32.RECT { Left = 0, Top = 0, Right = (int)wa.Width, Bottom = (int)wa.Height };
        }
        double scale = (gameWindow != 0 ? Win32.GetDpiForWindow(gameWindow) : 96) / 96.0;
        if (scale <= 0) scale = 1;

        w.UpdateLayout();
        double width = w.ActualWidth > 0 ? w.ActualWidth : w.Width;
        double height = w.ActualHeight > 0 ? w.ActualHeight : 400;
        double cx = cursor.X / scale, cy = cursor.Y / scale;
        double left = bounds.Left / scale, top = bounds.Top / scale, right = bounds.Right / scale, bottom = bounds.Bottom / scale;

        double x = cx - left > right - cx ? cx - width - 24 : cx + 24;
        double y = cy - height / 2;
        w.Left = Math.Clamp(x, left, Math.Max(left, right - width));
        w.Top = Math.Clamp(y, top, Math.Max(top, bottom - height));
    }
}
