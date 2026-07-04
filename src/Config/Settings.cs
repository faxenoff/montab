using System.Text.Json;
using System.Text.Json.Serialization;

namespace Montab.Config;

internal enum DockEdge
{
    Left,
    Right,
}

internal sealed class Settings
{
    public DockEdge Edge { get; set; } = DockEdge.Right;
    public double WidthPercent { get; set; } = 10;
    public string? Monitor { get; set; }

    public const double MinWidthPercent = 3;
    public const double MaxWidthPercent = 20;

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
