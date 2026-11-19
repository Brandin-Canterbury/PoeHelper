using System.Text.Json;

namespace PoeHelper.Core;

/// <summary>Per-user settings, stored in %APPDATA%\PoeHelper\settings.json. Each player enters their own.</summary>
public sealed class AppSettings
{
    public string League { get; set; } = "Covens";
    public string AccountName { get; set; } = "";   // with discriminator, e.g. Name#1234
    public string CharacterName { get; set; } = "";
    /// <summary>Optional contact (email or forum name) added to the User-Agent, as GGG asks of tools.</summary>
    public string Contact { get; set; } = "";
    /// <summary>"securable" = Instant Buyout only; "available" = Instant Buyout + in person; "online".</summary>
    public string PriceCheckStatus { get; set; } = "securable";
    public string PriceCheckHotkey { get; set; } = "Ctrl+D";
    public string CompareHotkey { get; set; } = "Alt+D";
    /// <summary>Gem name (or part of it) of the skill whose damage upgrades are scored for; blank = detect it from the links.</summary>
    public string MainSkill { get; set; } = "";
    /// <summary>Upgrade tracker: keys of the stats Find upgrade looks for (after resists). See TrackedStats.All.</summary>
    public List<string> TrackedStats { get; set; } = [.. Build.TrackedStats.Default];
    /// <summary>Find upgrade also counts chaos resistance as a resist to keep or raise.</summary>
    public bool UpgradeIncludeChaos { get; set; }
    /// <summary>Ctrl+Alt+C gives mod tiers, which picks better stats. Turn off if it misbehaves.</summary>
    public bool UseAdvancedCopy { get; set; } = true;
    /// <summary>
    /// Optional POESESSID, encrypted with Windows DPAPI for the current Windows user (never stored in plain text).
    /// Only ever sent to pathofexile.com. Lets trade searches use weighted stat groups.
    /// </summary>
    public string PoeSessionIdProtected { get; set; } = "";
    /// <summary>Saved ☰ button position in screen pixels (null = default spot).</summary>
    public int? MenuButtonX { get; set; }
    public int? MenuButtonY { get; set; }
    public double UpgradeBudget { get; set; } = 1;
    public string UpgradeBudgetCurrency { get; set; } = "divine";

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string AppDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PoeHelper");
    public static string CacheDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoeHelper", "cache");
    static string FilePath => Path.Combine(AppDataDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (JsonException) { }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppDataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }
}
