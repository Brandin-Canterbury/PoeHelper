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
    /// <summary>Ctrl+Alt+C gives mod tiers, which picks better stats. Turn off if it misbehaves.</summary>
    public bool UseAdvancedCopy { get; set; } = true;
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
