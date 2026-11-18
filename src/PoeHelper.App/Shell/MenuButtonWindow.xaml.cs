using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PoeHelper.App.Native;
using PoeHelper.Core;

namespace PoeHelper.App.Shell;

/// <summary>What the ☰ menu can ask the app to do.</summary>
public interface IMenuActions
{
    AppSettings Settings { get; }
    string StatusText { get; }
    bool CanReloadCharacter { get; }
    void ShowSettings();
    void ReloadCharacter();
    void RefreshTradeData();
    void OpenLog();
    void SaveSettings();
    void Quit();
}

/// <summary>
/// The always-on ☰ button (same pattern as the EVE helper): locked in place so it can't be nudged by
/// accident, Alt+drag moves it, and it never takes focus from the game. Its position is saved in real
/// screen pixels, because WPF units get unreliable across monitors with different scaling.
/// </summary>
public partial class MenuButtonWindow : Window
{
    const double EdgeMarginDips = 16;
    readonly IMenuActions _app;
    nint _hwnd;
    bool _dragged;
    DateTime _menuClosedAt;

    public MenuButtonWindow(IMenuActions app)
    {
        InitializeComponent();
        _app = app;
        Left = -10000;
        Top = -10000;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Win32.AddExStyle(_hwnd, Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW);
            HwndSource.FromHwnd(_hwnd)!.AddHook(Win32.AllowTinyWindowHook);
        };
        // WPF finishes sizing after Loaded and would undo a move made there.
        ContentRendered += (_, _) => PlaceAt(_app.Settings.MenuButtonX, _app.Settings.MenuButtonY);
    }

    /// <summary>Default: right edge, a third of the way down (clear of PoE's minimap and skill bar).</summary>
    internal static (int X, int Y) DefaultPosition(Win32.RECT work, int width, int height, int margin) =>
        (work.Right - width - margin, work.Top + (work.Bottom - work.Top) / 3 - height / 2);

    internal static bool IsOnScreen(int x, int y, int width, int height, Win32.RECT screen)
    {
        double cx = x + width / 2.0, cy = y + height / 2.0;
        return cx >= screen.Left && cx < screen.Right && cy >= screen.Top && cy < screen.Bottom;
    }

    void PlaceAt(int? sx, int? sy)
    {
        if (_hwnd == 0) return;
        Win32.GetWindowRect(_hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        int x, y;
        if (sx is { } px && sy is { } py && IsOnScreen(px, py, w, h, Win32.VirtualScreen()))
            (x, y) = (px, py);
        else
        {
            int margin = (int)Math.Round(EdgeMarginDips * VisualTreeHelper.GetDpi(this).DpiScaleX);
            (x, y) = DefaultPosition(Win32.PrimaryWorkArea(), w, h, margin);
        }
        Win32.SetWindowPos(_hwnd, 0, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
    }

    void Face_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragged = false;
        if (!Win32.IsDown(Win32.VK_MENU)) return;
        Menu.IsOpen = false;
        DragMove();
        _dragged = true;
        Win32.GetWindowRect(_hwnd, out var r);
        _app.Settings.MenuButtonX = r.Left;
        _app.Settings.MenuButtonY = r.Top;
        _app.SaveSettings();
        e.Handled = true;
    }

    void Face_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragged) return;
        // A click that just closed the menu shouldn't reopen it.
        if (DateTime.UtcNow - _menuClosedAt < TimeSpan.FromMilliseconds(250)) return;
        StatusLine.Text = _app.StatusText;
        ReloadRow.IsEnabled = _app.CanReloadCharacter;
        Menu.IsOpen = true;
    }

    void Menu_Closed(object? sender, EventArgs e) => _menuClosedAt = DateTime.UtcNow;
    void Face_MouseEnter(object sender, MouseEventArgs e) => Face.Background = (Brush)FindResource("ControlHover");
    void Face_MouseLeave(object sender, MouseEventArgs e) => Face.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1E, 0x1A, 0x15));

    void Run(Action a) { Menu.IsOpen = false; a(); }
    void Settings_Click(object sender, RoutedEventArgs e) => Run(_app.ShowSettings);
    void Reload_Click(object sender, RoutedEventArgs e) => Run(_app.ReloadCharacter);
    void RefreshData_Click(object sender, RoutedEventArgs e) => Run(_app.RefreshTradeData);
    void Log_Click(object sender, RoutedEventArgs e) => Run(_app.OpenLog);
    void Quit_Click(object sender, RoutedEventArgs e) => Run(_app.Quit);

    void ResetPosition_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        _app.Settings.MenuButtonX = null;
        _app.Settings.MenuButtonY = null;
        _app.SaveSettings();
        PlaceAt(null, null);
    });
}
