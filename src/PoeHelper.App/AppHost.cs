using System.Diagnostics;
using System.Windows;
using PoeHelper.App.Native;
using PoeHelper.App.Overlay;
using PoeHelper.App.Shell;
using PoeHelper.Core;
using PoeHelper.Core.Build;
using PoeHelper.Core.Data;
using PoeHelper.Core.Items;
using PoeHelper.Core.Pricing;
using PoeHelper.Core.Trade;
using Forms = System.Windows.Forms;

namespace PoeHelper.App;

/// <summary>Owns the long-lived pieces: settings, API client, trade data, keyboard hook, tray icon and overlays.</summary>
public sealed class AppHost : IDisposable, IMenuActions
{
    AppSettings _settings = AppSettings.Load();
    TradeClient _client;
    Task<TradeData>? _data;
    readonly CurrencyRates _rates = new();
    KeyboardHook? _hook;
    PriceOverlay? _priceOverlay;
    CompareOverlay? _compareOverlay;
    Task<CharacterBuild>? _build;
    Forms.NotifyIcon? _tray;
    MenuButtonWindow? _menu;
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
        _compareOverlay = new CompareOverlay();
        _compareOverlay.ReloadRequested += () => _ = ReloadAndRecompareAsync();
        _compareOverlay.FindUpgradeRequested += () => _ = FindUpgradeAsync();
        CreateTray();
        _menu = new MenuButtonWindow(this);
        _menu.Show();
        _hook = new KeyboardHook { OverlayOpen = () => _priceOverlay?.IsVisible == true || _compareOverlay?.IsVisible == true };
        _hook.Escape += HideOverlays;
        BindHotkeys();
        _data = LoadDataAsync();
        _ = LoadLeaguesAsync();
        if (CanReloadCharacter) _build = LoadBuildAsync();

        if (string.IsNullOrWhiteSpace(_settings.AccountName))
            _tray!.ShowBalloonTip(5000, "PoeHelper is running",
                $"{_settings.PriceCheckHotkey}: price check the hovered item. Use the ☰ button (right edge of the screen) for settings: add your character for gear compare.",
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
        _compareOverlay?.Hide();
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

    // ---------------------------------------------------------------- gear compare

    Task<CharacterBuild> LoadBuildAsync() => Task.Run(async () =>
    {
        try
        {
            var b = await CharacterBuild.LoadAsync(_client, _settings.AccountName, _settings.CharacterName, AppSettings.CacheDir);
            Log.Info($"Loaded character {b.Name} L{b.Level} {b.ClassName} ({b.Items.Count} items, {b.TreeStats.Count} tree lines)");
            return b;
        }
        catch (Exception ex) { Log.Error("Loading character failed", ex); throw; }
    });

    async Task CompareAsync()
    {
        var game = Win32.PoeForegroundWindow();
        if (game == 0) return;
        var text = await ItemCopier.CopyHoveredItemAsync(_settings.UseAdvancedCopy, TimeSpan.FromMilliseconds(600));
        if (text is null) { HideOverlays(); return; }
        Log.Info("Compare:\n" + text);
        _priceOverlay?.Hide();
        var overlay = _compareOverlay!;
        await ShowCompareAsync(ItemParser.Parse(text));
        overlay.ShowNearCursor(game);
    }

    async Task ShowCompareAsync(ParsedItem item)
    {
        var overlay = _compareOverlay!;
        if (!CanReloadCharacter || _build is null)
        {
            overlay.Model.ShowMessage("Gear compare", "Add your account name (with #1234) and character in Settings (☰ menu) first.");
            return;
        }
        try
        {
            var build = await _build;
            if (item.TypeLine is not null && _data is not null) item.BaseType = (await _data).ResolveBaseType(item.TypeLine);
            var result = GearCompare.Compare(build, item);
            if (result.Count == 0)
                overlay.Model.ShowMessage(item.DisplayName, "Compare works on equipment: weapons, armour, jewellery and belts. Jewels, flasks and gems aren't supported yet.");
            else
            {
                overlay.Model.Budget = _settings.UpgradeBudget.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                overlay.Model.BudgetInDivines = _settings.UpgradeBudgetCurrency != "chaos";
                overlay.Model.Show(item, result, build);
            }
        }
        catch (Exception ex)
        {
            _build = null; // let the next press retry
            if (CanReloadCharacter) _build = LoadBuildAsync();
            overlay.Model.ShowMessage("Gear compare", "Couldn't load your character: " + Friendly(ex));
        }
    }

    async Task ReloadAndRecompareAsync()
    {
        if (!CanReloadCharacter) return;
        _build = LoadBuildAsync();
        if (_compareOverlay?.Model.Item is { } item)
        {
            _compareOverlay.Model.Busy = true;
            await ShowCompareAsync(item);
        }
    }

    async Task FindUpgradeAsync()
    {
        var m = _compareOverlay?.Model;
        if (m?.Selected is not { } cmp || _build is null || _data is null) return;
        if (!double.TryParse(m.Budget, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var budget) || budget <= 0)
        {
            m.UpgradeStatus = "Enter a budget, e.g. 2 (div) or 150 (c).";
            return;
        }
        string currency = m.BudgetInDivines ? "divine" : "chaos";
        _settings.UpgradeBudget = budget;
        _settings.UpgradeBudgetCurrency = currency;
        _settings.Save();
        m.ClearUpgrades();
        m.UpgradeStatus = "Searching Instant Buyout listings…";
        try
        {
            var r = await UpgradeFinder.RunAsync(_client, await _data, _rates, await _build, cmp.Slot, budget, currency, _settings.League,
                m.PrioritiseLife ? UpgradePriority.Life : UpgradePriority.Damage);
            Log.Info($"Upgrade search {cmp.Slot}: {r.Total} listed, {r.Candidates.Count} kept, {r.Rejected} rejected");
            m.ShowUpgrades(r);
        }
        catch (Exception ex)
        {
            Log.Error("Upgrade search failed", ex);
            m.UpgradeStatus = Friendly(ex);
        }
    }

    /// <summary>QA path: compare a saved item text against the configured character and save the overlay as a PNG.</summary>
    public async Task RenderCompareAsync(string itemFile, string pngPath, bool runUpgrade = false)
    {
        _compareOverlay = new CompareOverlay();
        _client.Limiter.MaxWait = TimeSpan.FromSeconds(90);
        _data = LoadDataAsync();
        _build = LoadBuildAsync();
        await ShowCompareAsync(ItemParser.Parse(await File.ReadAllTextAsync(itemFile)));
        if (runUpgrade) await FindUpgradeAsync();
        _compareOverlay.Left = 50; _compareOverlay.Top = 50;
        _compareOverlay.Show();
        await Task.Delay(300);
        _compareOverlay.UpdateLayout();
        OverlaySnapshot.Save(_compareOverlay, pngPath);
        _compareOverlay.Close();
    }

    // ---------------------------------------------------------------- tray / settings

    void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
        menu.Items.Add("Open log", null, (_, _) => OpenFile(Log.FilePath));
        menu.Items.Add("Refresh trade data", null, (_, _) => RefreshData());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Quit());
        _tray = new Forms.NotifyIcon
        {
            Icon = TrayIcon.Create(),
            Text = "PoeHelper",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => OpenSettings();
    }

    // ---------------------------------------------------------------- ☰ menu

    public AppSettings Settings => _settings;
    public string StatusText => string.IsNullOrWhiteSpace(_settings.CharacterName)
        ? $"{_settings.League} · no character set"
        : $"{_settings.League} · {_settings.CharacterName}";
    public bool CanReloadCharacter => !string.IsNullOrWhiteSpace(_settings.AccountName) && !string.IsNullOrWhiteSpace(_settings.CharacterName);
    public void ShowSettings() => OpenSettings();
    public void ReloadCharacter() { if (CanReloadCharacter) _build = LoadBuildAsync(); }
    public void RefreshTradeData() => RefreshData();
    public void OpenLog() => OpenFile(Log.FilePath);
    public void SaveSettings() => _settings.Save();
    public void Quit() => Application.Current.Shutdown();

    void OpenSettings()
    {
        var w = new SettingsWindow(_settings, _leagues);
        if (w.ShowDialog() != true) return;
        _client.Dispose();
        _client = new TradeClient(TradeClient.BuildUserAgent(_settings.Contact));
        BindHotkeys();
        _build = CanReloadCharacter ? LoadBuildAsync() : null;
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

    void HideOverlays()
    {
        _priceOverlay?.Hide();
        _compareOverlay?.Hide();
    }

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
        _menu?.Close();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        _client.Dispose();
    }
}
