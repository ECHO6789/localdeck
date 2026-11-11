using System.Security.Cryptography;
using System.Text;

namespace LocalDeck.App.Services;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalDeck");

    public static string DatabasePath => Path.Combine(DataDirectory, "localdeck.db");
    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public static string LogPath => Path.Combine(DataDirectory, "localdeck.log");
    public static string ScreenshotDirectory => Path.Combine(DataDirectory, "screenshots");
    public static string ProfileDirectory => Path.Combine(DataDirectory, "profiles");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ScreenshotDirectory);
        Directory.CreateDirectory(ProfileDirectory);
    }

    public static string StableId(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes.AsSpan(0, 6)).ToLowerInvariant();
    }

    public static void Log(string message)
    {
        try
        {
            EnsureCreated();
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never break the desktop tool.
        }
    }
}
