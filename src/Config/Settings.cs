using System.Text.Json;
using System.Text.Json.Serialization;

namespace Montab.Config;

internal enum DockEdge
{
    Left,
    Right,
}

/// <summary>Панель одного монитора: сторона, ширина, показывать ли её вообще.</summary>
internal sealed class MonitorSettings
{
    /// <summary>Имя устройства из MONITORINFOEXW (\\.\DISPLAY1) — ключ настроек.</summary>
    public string Device { get; set; } = "";

    public DockEdge Edge { get; set; } = DockEdge.Right;
    public double WidthPercent { get; set; } = Settings.DefaultWidthPercent;
    public bool Enabled { get; set; } = true;
}

internal sealed class Settings
{
    public const double DefaultWidthPercent = 10;
    public const double MinWidthPercent = 3;
    // До половины экрана: на вертикальных мониторах широкую панель реально используют
    public const double MaxWidthPercent = 50;

    /// <summary>Панели по мониторам; запись для монитора заводится при первом его появлении.</summary>
    public List<MonitorSettings> Monitors { get; set; } = [];

    // Поля панели-одиночки (версии до мультимонитора): читаются из старого
    // файла настроек и служат значениями по умолчанию для новых мониторов.
    public DockEdge Edge { get; set; } = DockEdge.Right;
    public double WidthPercent { get; set; } = DefaultWidthPercent;

    /// <summary>Настройки монитора; отсутствующие заводятся на лету по образцу первой панели.</summary>
    public MonitorSettings For(string device)
    {
        foreach (var mon in Monitors)
        {
            if (mon.Device == device)
                return mon;
        }

        // Новый монитор наследует геометрию уже настроенной панели, а не голый дефолт
        var template = Monitors.Count > 0 ? Monitors[0] : null;
        var created = new MonitorSettings
        {
            Device = device,
            Edge = template?.Edge ?? Edge,
            WidthPercent = template?.WidthPercent ?? WidthPercent,
        };
        Monitors.Add(created);
        return created;
    }

    static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "montab");

    static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize(
                    File.ReadAllText(FilePath), SettingsContext.Default.Settings);
                if (loaded is not null)
                {
                    loaded.WidthPercent = Math.Clamp(loaded.WidthPercent, MinWidthPercent, MaxWidthPercent);
                    foreach (var mon in loaded.Monitors)
                        mon.WidthPercent = Math.Clamp(mon.WidthPercent, MinWidthPercent, MaxWidthPercent);
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SettingsContext.Default.Settings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(Settings))]
internal sealed partial class SettingsContext : JsonSerializerContext;
