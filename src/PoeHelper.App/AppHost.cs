using System.Diagnostics;
using System.Windows;
using PoeHelper.App.Native;
using PoeHelper.App.Overlay;
using PoeHelper.Core;
using PoeHelper.Core.Data;
using PoeHelper.Core.Items;
using PoeHelper.Core.Pricing;
using PoeHelper.Core.Trade;
using Forms = System.Windows.Forms;

namespace PoeHelper.App;

/// <summary>Owns the long-lived pieces: settings, API client, trade data, keyboard hook, tray icon and overlays.</summary>
public sealed class AppHost : IDisposable
{
    AppSettings _settings = AppSettings.Load();
    TradeClient _client;
    Task<TradeData>? _data;
    readonly CurrencyRates _rates = new();
    KeyboardHook? _hook;
    PriceOverlay? _priceOverlay;
    Forms.NotifyIcon? _tray;
    CancellationTokenSource? _running;
    List<string> _leagues = ["Covens", "Hardcore Covens", "Standard", "Hardcore"];

    public AppHost()
    {
        _client = new TradeClient(TradeClient.BuildUserAgent(_settings.Contact));
    }

    public async Task StartAsync()
    {
        Log.Info("Starting");
        _priceOverlay = new PriceOverlay();
        _priceOverlay.SearchAgainRequested += () => _ = RerunAsync();
        CreateTray();
        _hook = new KeyboardHook { OverlayOpen = () => _priceOverlay?.IsVisible == true };
        _hook.Escape += HideOverlays;
        BindHotkeys();
        _data = LoadDataAsync();
        _ = LoadLeaguesAsync();

        if (string.IsNullOrWhiteSpace(_settings.AccountName))
            _tray!.ShowBalloonTip(5000, "PoeHelper is running",
                $"{_settings.PriceCheckHotkey}: price check the hovered item. Open settings from the tray to add your character for gear compare.",
                Forms.ToolTipIcon.Info);
        await Task.CompletedTask;
    }

    Task<TradeData> LoadDataAsync() => Task.Run(async () =>
    {
        try { return await TradeData.LoadAsync(_client, AppSettings.CacheDir); }
        catch (Exception ex) { Log.Error("Loading trade data failed", ex); throw; }
    });

    async Task LoadLeaguesAsync()
    {
        try
        {
            var json = await _client.GetDataAsync("leagues");
            var ids = json["result"]!.AsArray().Select(n => n!["id"]!.GetValue<string>()).Distinct().ToList();
            if (ids.Count > 0) _leagues = ids;
        }
        catch (Exception ex) { Log.Error("Loading leagues failed", ex); }
    }

    void BindHotkeys()
    {
        _hook!.ClearBindings();
        try
        {
            _hook.Bind(Hotkey.Parse(_settings.PriceCheckHotkey), () => _ = PriceCheckAsync());
            _hook.Bind(Hotkey.Parse(_settings.CompareHotkey), () => _ = CompareAsync());
        }
        catch (FormatException ex)
        {
            Log.Error("Bad hotkey", ex);
            MessageBox.Show(ex.Message, "PoeHelper hotkeys");
        }
    }

    // ---------------------------------------------------------------- price check

    async Task PriceCheckAsync()
    {
        var game = Win32.PoeForegroundWindow();
        if (game == 0) return;
        var text = await ItemCopier.CopyHoveredItemAsync(_settings.UseAdvancedCopy, TimeSpan.FromMilliseconds(600));
        if (text is null)
        {
            HideOverlays(); // hotkey with nothing hovered = close
            return;
        }
        Log.Info("Price check:\n" + text);
        var overlay = _priceOverlay!;
        PriceQuery query;
        try
        {
            var data = await _data!;
            query = PriceQueryBuilder.Build(ItemParser.Parse(text), data, _settings.PriceCheckStatus);
        }
        catch (Exception ex)
        {
            overlay.Model.Title = "Price check";
            overlay.Model.ShowError(Friendly(ex));
            overlay.ShowNearCursor(game);
            return;
        }
        overlay.Model.BeginQuery(query);
        overlay.ShowNearCursor(game);
        await RunQueryAsync(query);
    }

    async Task RerunAsync()
    {
        if (_priceOverlay?.Model.Query is not { } query) return;
        _priceOverlay.Model.BeginQuery(query);
        await RunQueryAsync(query);
    }

    async Task RunQueryAsync(PriceQuery query)
    {
        _running?.Cancel();
        var cts = _running = new CancellationTokenSource();
        var overlay = _priceOverlay!;
        try
        {
            var service = new PriceCheckService(_client, await _data!, _rates);
            var result = await service.RunAsync(query, _settings.League, cts.Token);
            if (!cts.IsCancellationRequested) overlay.Model.ShowResult(result);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Price check failed", ex);
            if (!cts.IsCancellationRequested) overlay.Model.ShowError(Friendly(ex));
        }
    }

    /// <summary>QA path: price-check a saved item text and save the overlay as a PNG.</summary>
    public async Task RenderCheckAsync(string itemFile, string pngPath)
    {
        _priceOverlay = new PriceOverlay();
        _client.Limiter.MaxWait = TimeSpan.FromSeconds(90);
        _data = LoadDataAsync();
        var query = PriceQueryBuilder.Build(ItemParser.Parse(await File.ReadAllTextAsync(itemFile)), await _data, _settings.PriceCheckStatus);
        _priceOverlay.Model.BeginQuery(query);
        _priceOverlay.Left = 50; _priceOverlay.Top = 50;
        _priceOverlay.Show();
        await RunQueryAsync(query);
        await Task.Delay(300);
        _priceOverlay.UpdateLayout();
        OverlaySnapshot.Save(_priceOverlay, pngPath);
        _priceOverlay.Close();
    }

    // ---------------------------------------------------------------- compare (next step)

    async Task CompareAsync()
    {
        var game = Win32.PoeForegroundWindow();
        if (game == 0) return;
        var overlay = _priceOverlay!;
        overlay.Model.Title = "Gear compare";
        overlay.Model.ShowError("Gear compare isn't built yet. Price check works now.");
        overlay.ShowNearCursor(game);
        await Task.CompletedTask;
    }

    // ---------------------------------------------------------------- tray / settings

    void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
        menu.Items.Add("Open log", null, (_, _) => OpenFile(Log.FilePath));
        menu.Items.Add("Refresh trade data", null, (_, _) => RefreshData());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Application.Current.Shutdown());
        _tray = new Forms.NotifyIcon
        {
            Icon = TrayIcon.Create(),
            Text = "PoeHelper",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => OpenSettings();
    }

    void OpenSettings()
    {
        var w = new SettingsWindow(_settings, _leagues);
        if (w.ShowDialog() != true) return;
        _client.Dispose();
        _client = new TradeClient(TradeClient.BuildUserAgent(_settings.Contact));
        BindHotkeys();
    }

    void RefreshData()
    {
        try { Directory.Delete(AppSettings.CacheDir, true); } catch (IOException) { }
        _data = LoadDataAsync();
    }

    static void OpenFile(string path)
    {
        if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    void HideOverlays() => _priceOverlay?.Hide();

    static string Friendly(Exception ex) => ex switch
    {
        RateLimitedException r => r.Message,
        TradeApiException t => t.Message,
        HttpRequestException => "Can't reach pathofexile.com. Check your connection.",
        TaskCanceledException => "The trade site took too long to answer. Try again.",
        FormatException f => f.Message,
        _ => $"Something went wrong: {ex.Message} (details in the log)",
    };

    public void Dispose()
    {
        _hook?.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        _client.Dispose();
    }
}
