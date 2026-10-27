using System.Text.Json;

namespace DSPRE.Core;

/// <summary>The user's app preferences (legacy DS_Map/SettingsManager.cs's userSettings.json, for the
/// settings that apply here). Kept per user, next to the logs, not in any project.</summary>
public sealed class AppSettings
{
    /// <summary>Where "Save ROM As" starts (legacy exportPath).</summary>
    public string RomExportFolder { get; set; } = "";

    /// <summary>Where file pickers for imports start (legacy mapImportStarterPoint).</summary>
    public string ImportFolder { get; set; } = "";

    /// <summary>A project folder to open on launch (legacy openDefaultRom).</summary>
    public string StartupProject { get; set; } = "";

    public bool OpenStartupProjectWithoutAsking { get; set; }

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSPRE Reloaded", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>The saved settings, or the defaults when there are none or the file cannot be read.</summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            StatusLog.Warning("Settings", $"{FilePath} could not be read, so the defaults are used: {ex.Message}");
        }

        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
