using PoeHelper.Core;

namespace PoeHelper.App;

/// <summary>Plain text log at %APPDATA%\PoeHelper\log.txt (trimmed at 1 MB). Item texts are logged for bug reports.</summary>
public static class Log
{
    static readonly object Gate = new();
    public static string FilePath => Path.Combine(AppSettings.AppDataDir, "log.txt");

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.AppDataDir);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000) File.Delete(FilePath);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
    }
}
