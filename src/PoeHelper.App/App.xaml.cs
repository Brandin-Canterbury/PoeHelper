using System.Windows;

namespace PoeHelper.App;

public partial class App : Application
{
    Mutex? _single;
    AppHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new Mutex(true, "PoeHelper.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("PoeHelper is already running (see the tray icon).", "PoeHelper");
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            args.Handled = true;
        };
        _host = new AppHost();
        // Dev/QA: PoeHelper.exe --check <item.txt> <out.png>  runs one price check and renders the overlay.
        if (e.Args.Length == 3 && e.Args[0] == "--check")
        {
            try { await _host.RenderCheckAsync(e.Args[1], e.Args[2]); }
            catch (Exception ex) { Log.Error("--check failed", ex); }
            Shutdown();
            return;
        }
        await _host.StartAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }
}
