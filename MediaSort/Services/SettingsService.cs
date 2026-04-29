using System;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using MediaSort.Models;

namespace MediaSort.Services;

public static class SettingsService
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MediaSort");
    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    public static string SettingsFilePath => SettingsPath;

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                CrashLogger.Info($"settings:load no-file path={SettingsPath}");
                return new AppSettings();
            }
            var json = File.ReadAllText(SettingsPath);
            var s = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();

            // One-time migration: prior versions defaulted ThemeOverride to System,
            // which produced a white client area on Windows installs in Light mode.
            // The app now defaults to Dark; flip any never-explicitly-set System value
            // forward so existing users see the new dark theme immediately.
            if (s.ThemeOverride == ThemeOverride.System)
            {
                s.ThemeOverride = ThemeOverride.Dark;
                CrashLogger.Info("settings:migrate ThemeOverride System -> Dark");
            }

            CrashLogger.Info($"settings:load ok dests={s.Destinations.Count} path={SettingsPath}");
            return s;
        }
        catch (Exception ex)
        {
            CrashLogger.Info($"settings:load FAIL {ex.GetType().Name}: {ex.Message}");
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
            CrashLogger.Info($"settings:save ok dests={settings.Destinations.Count} bytes={json.Length}");
        }
        catch (Exception ex)
        {
            CrashLogger.Info($"settings:save FAIL {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static SerializableDestination ToSerializable(DestinationButton d) => new()
    {
        Name = d.Name,
        FolderPath = d.FolderPath,
        HotKey = d.HotKey.ToString(),
        Modifiers = d.Modifiers.ToString(),
        KindFilter = d.KindFilter,
        SubfolderTemplate = d.SubfolderTemplate,
        RenameTemplate = d.RenameTemplate,
        ActionOverride = d.ActionOverride.HasValue ? d.ActionOverride.Value.ToString() : "",
        AccentColor = d.AccentColor ?? ""
    };

    public static DestinationButton FromSerializable(SerializableDestination s)
    {
        var d = new DestinationButton
        {
            Name = s.Name,
            FolderPath = s.FolderPath,
            KindFilter = s.KindFilter ?? "",
            SubfolderTemplate = s.SubfolderTemplate ?? "",
            RenameTemplate = s.RenameTemplate ?? "",
            AccentColor = s.AccentColor ?? ""
        };
        if (Enum.TryParse<Key>(s.HotKey, out var k)) d.HotKey = k;
        if (Enum.TryParse<ModifierKeys>(s.Modifiers, out var m)) d.Modifiers = m;
        if (!string.IsNullOrWhiteSpace(s.ActionOverride) &&
            Enum.TryParse<FileAction>(s.ActionOverride, out var fa))
            d.ActionOverride = fa;
        return d;
    }
}
