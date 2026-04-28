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

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
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
        }
        catch
        {
            // best-effort
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
        RenameTemplate = d.RenameTemplate
    };

    public static DestinationButton FromSerializable(SerializableDestination s)
    {
        var d = new DestinationButton
        {
            Name = s.Name,
            FolderPath = s.FolderPath,
            KindFilter = s.KindFilter ?? "",
            SubfolderTemplate = s.SubfolderTemplate ?? "",
            RenameTemplate = s.RenameTemplate ?? ""
        };
        if (Enum.TryParse<Key>(s.HotKey, out var k)) d.HotKey = k;
        if (Enum.TryParse<ModifierKeys>(s.Modifiers, out var m)) d.Modifiers = m;
        return d;
    }
}
