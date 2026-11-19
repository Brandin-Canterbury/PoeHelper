using System.Windows;

namespace PoeHelper.App;

public partial class App : Application
{
    Mutex? _single;
    AppHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            args.Handled = true;
        };

        // Dev/QA: PoeHelper.exe --check <item.txt> <out.png>  runs one price check and renders the overlay.
        // It registers no hotkeys or tray icon, so it may run beside the normal instance.
        if (e.Args.Length == 3 && e.Args[0] is "--check" or "--compare" or "--upgrade")
        {
            _host = new AppHost();
            try
            {
                if (e.Args[0] == "--check") await _host.RenderCheckAsync(e.Args[1], e.Args[2]);
                else await _host.RenderCompareAsync(e.Args[1], e.Args[2], e.Args[0] == "--upgrade");
            }
            catch (Exception ex) { Log.Error("--check failed", ex); }
            Shutdown();
            return;
        }

        // Dev/QA: PoeHelper.exe --window help|settings <out.png>  renders a dialog to PNG.
        if (e.Args.Length == 3 && e.Args[0] == "--window")
        {
            Window w = e.Args[1] == "help" ? new SessionHelpWindow() : new SettingsWindow(Core.AppSettings.Load(), ["Covens"]);
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = 40; w.Top = 40;
            w.Show();
            await Task.Delay(400);
            Overlay.OverlaySnapshot.Save(w, e.Args[2]);
            w.Close();
            Shutdown();
            return;
        }

        _single = new Mutex(true, "PoeHelper.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("PoeHelper is already running (see the tray icon).", "PoeHelper");
            Shutdown();
            return;
        }
        _host = new AppHost();
        await _host.StartAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }
}
