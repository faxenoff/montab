using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;

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

    const string FolderName = "\\montab";
    const string FileName = "\\settings.json";

    /// <summary>Папка настроек в %APPDATA%; null — система не отдала путь к профилю.</summary>
    static unsafe string? Dir
    {
        get
        {
            Guid folder = PInvoke.FOLDERID_RoamingAppData;
            PWSTR path = default;
            string? dir = PInvoke.SHGetKnownFolderPath(&folder, 0, default, &path).Succeeded
                ? path.ToString() + FolderName
                : null;
            PInvoke.CoTaskMemFree(path);
            return dir;
        }
    }

    public static Settings Load()
    {
        string? dir = Dir;
        var text = dir is null ? null : TextFile.Read(dir + FileName);
        if (text is not null)
        {
            try
            {
                var loaded = Parse(text);
                loaded.WidthPercent = Math.Clamp(loaded.WidthPercent, MinWidthPercent, MaxWidthPercent);
                foreach (var mon in loaded.Monitors)
                    mon.WidthPercent = Math.Clamp(mon.WidthPercent, MinWidthPercent, MaxWidthPercent);
                return loaded;
            }
            catch (FormatException)
            {
            }
        }

        return new Settings();
    }

    public unsafe void Save()
    {
        string? dir = Dir;
        if (dir is null)
            return;
        fixed (char* p = dir)
            PInvoke.CreateDirectory(p, null);
        TextFile.Write(dir + FileName, ToJson());
    }

    // Формат файла — тот же, что писал System.Text.Json (отступ 2 пробела, enum
    // строкой): файлы прежних версий читаются, новые читаются прежними версиями.
    // Отличие одно: дробные числа пишутся с точностью до шести знаков.

    /// <summary>Разбор settings.json; неизвестные поля пропускаются, ошибка формата — FormatException.</summary>
    internal static Settings Parse(ReadOnlySpan<char> text)
    {
        var json = new JsonReader(text);
        var settings = new Settings();
        json.Expect('{');
        bool first = true;
        while (json.NextElement('}', ref first))
        {
            switch (json.ReadPropertyName())
            {
                case "Monitors":
                    settings.Monitors.Clear();
                    json.Expect('[');
                    bool firstMon = true;
                    while (json.NextElement(']', ref firstMon))
                        settings.Monitors.Add(ParseMonitor(ref json));
                    break;
                case "Edge": settings.Edge = ParseEdge(ref json); break;
                case "WidthPercent": settings.WidthPercent = json.ReadNumber(); break;
                default: json.SkipValue(); break;
            }
        }
        json.ExpectEnd();
        return settings;
    }

    static MonitorSettings ParseMonitor(ref JsonReader json)
    {
        var mon = new MonitorSettings();
        json.Expect('{');
        bool first = true;
        while (json.NextElement('}', ref first))
        {
            switch (json.ReadPropertyName())
            {
                case "Device": mon.Device = json.ReadString(); break;
                case "Edge": mon.Edge = ParseEdge(ref json); break;
                case "WidthPercent": mon.WidthPercent = json.ReadNumber(); break;
                case "Enabled": mon.Enabled = json.ReadBool(); break;
                default: json.SkipValue(); break;
            }
        }
        return mon;
    }

    static DockEdge ParseEdge(ref JsonReader json)
    {
        string name = json.ReadString();
        if (name.Equals(LeftName, StringComparison.OrdinalIgnoreCase))
            return DockEdge.Left;
        if (name.Equals(RightName, StringComparison.OrdinalIgnoreCase))
            return DockEdge.Right;
        throw new FormatException();
    }

    const string LeftName = "Left";
    const string RightName = "Right";

    static string EdgeName(DockEdge edge) => edge == DockEdge.Left ? LeftName : RightName;

    internal string ToJson()
    {
        var sb = new StringBuilder(256);
        sb.Append("{\r\n  \"Monitors\": [");
        for (int i = 0; i < Monitors.Count; i++)
        {
            var mon = Monitors[i];
            sb.Append(i == 0 ? "\r\n    {\r\n      \"Device\": " : ",\r\n    {\r\n      \"Device\": ");
            JsonWriter.AppendString(sb, mon.Device);
            sb.Append(",\r\n      \"Edge\": \"").Append(EdgeName(mon.Edge));
            sb.Append("\",\r\n      \"WidthPercent\": ");
            JsonWriter.AppendNumber(sb, mon.WidthPercent);
            sb.Append(",\r\n      \"Enabled\": ").Append(mon.Enabled ? "true" : "false");
            sb.Append("\r\n    }");
        }
        sb.Append(Monitors.Count > 0 ? "\r\n  ],\r\n  \"Edge\": \"" : "],\r\n  \"Edge\": \"").Append(EdgeName(Edge));
        sb.Append("\",\r\n  \"WidthPercent\": ");
        JsonWriter.AppendNumber(sb, WidthPercent);
        sb.Append("\r\n}");
        return sb.ToString();
    }
}
