using System.Text.Json;
using System.Text.Json.Serialization;
using BambuBar.Core.Models;

namespace BambuBar.Core.Store;

/// <summary>
/// The last jobs the app saw, for "Last: …" and finish times — only what the app saw while running.
/// Stored at <c>%APPDATA%\Bambu Bar\history.json</c> (the macOS original keeps it in Application Support).
/// </summary>
public static class HistoryStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string? FilePath
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return appData.Length > 0 ? Path.Combine(appData, "Bambu Bar", "history.json") : null;
        }
    }

    public static List<JobRecord> Load()
    {
        try
        {
            if (FilePath is not { } path || !File.Exists(path)) return new List<JobRecord>();
            var list = JsonSerializer.Deserialize<List<JobRecord>>(File.ReadAllText(path), Options);
            return list ?? new List<JobRecord>();
        }
        catch
        {
            return new List<JobRecord>();
        }
    }

    public static void Save(List<JobRecord> history)
    {
        try
        {
            if (FilePath is not { } path) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(history, Options));
        }
        catch
        {
            // History is a convenience; failing to write it should not surface to the user.
        }
    }
}

/// <summary>
/// Printers, preferences and settings. The macOS original uses user defaults; on Windows a JSON file
/// under <c>%APPDATA%\Bambu Bar\settings.json</c>. Access codes never live here — those are in the
/// Credential Manager.
/// </summary>
public sealed class SettingsStore
{
    public enum MenuBarStyle
    {
        Icon,
        Percent,
        Remaining,
        Both,
    }

    public enum ThemeMode
    {
        /// <summary>Follow the Windows system appearance (live).</summary>
        Auto,
        Light,
        Dark,
    }

    public List<PrinterConfig> Printers { get; set; } = new();
    public string? SelectedSerial { get; set; }
    /// <summary>Null = automatic.</summary>
    public string? MenuBarSerial { get; set; }
    public MenuBarStyle MenuBarStyleSetting { get; set; } = MenuBarStyle.Both;
    public bool NotifyFinished { get; set; } = true;
    public bool NotifyFailed { get; set; } = true;
    public bool NotifyPaused { get; set; } = true;
    public bool NotifyStarted { get; set; } = false;
    public bool ConfirmStop { get; set; } = true;
    public bool OpenAtLogin { get; set; }
    public ThemeMode ThemeModeSetting { get; set; } = ThemeMode.Auto;
    /// <summary>Language code (e.g. "fi"); null = follow the Windows system language.</summary>
    public string? LanguageSetting { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string? SettingsPath
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return appData.Length > 0 ? Path.Combine(appData, "Bambu Bar", "settings.json") : null;
        }
    }

    public static SettingsStore Load()
    {
        try
        {
            if (SettingsPath is { } path && File.Exists(path))
                return JsonSerializer.Deserialize<SettingsStore>(File.ReadAllText(path), Options) ?? new SettingsStore();
        }
        catch
        {
            // Fall back to defaults rather than lose the app to a bad settings file.
        }
        return new SettingsStore();
    }

    public void Save()
    {
        try
        {
            if (SettingsPath is not { } path) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // Best effort.
        }
    }
}
