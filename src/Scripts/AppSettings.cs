using System.Text.Json;
using System.Text.Json.Serialization;
using Jesnote.Core;

namespace Jesnote;

public enum ColorThemePreference
{
    Auto,
    Light,
    Dark,
}

public sealed class AppSettings
{
    public LanguagePreference Language { get; set; } = LanguagePreference.Auto;
    public ColorThemePreference ColorTheme { get; set; } = ColorThemePreference.Auto;
    public StringStorageMode StringStorage { get; set; } = StringStorageMode.Compact;
    public bool ExtensionFilter { get; set; } = true;
    public bool NotifyUpdates { get; set; } = true;
    public int RecentFileCount { get; set; } = 5;
    public List<string> RecentFiles { get; set; } = [];
    public int LastWindowWidth { get; set; } = 900;
    public int LastWindowHeight { get; set; } = 650;
    public bool LastDetailShown { get; set; } = true;
    public bool LastSelectionShown { get; set; }

    [JsonIgnore]
    public static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Jesnote",
            "settings.json"
        );

    internal static readonly JsonSerializerOptions Options =
        new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() },
        };

    public static AppSettings Load()
    {
        AppSettings defaults;
        try
        {
            defaults = AppInfo.CreateDefaultSettings();
        }
        catch
        {
            defaults = new AppSettings();
        }

        try
        {
            if (!File.Exists(SettingsPath))
                return defaults;
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? defaults;
        }
        catch
        {
            return defaults;
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // best-effort; not fatal
        }
    }

    public void PushRecentFile(string path)
    {
        var max = Math.Max(1, RecentFileCount);
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > max)
            RecentFiles.RemoveRange(max, RecentFiles.Count - max);
    }
}
