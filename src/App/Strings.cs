using Windows.Win32;

namespace Montab.App;

/// <summary>
/// Строки интерфейса: русский при русском языке системы, иначе английский.
/// (InvariantGlobalization=true — CultureInfo недоступна, язык берём у Win32.)
/// </summary>
internal static class Strings
{
    const int LangRussian = 0x19; // PRIMARYLANGID русского языка

    static readonly bool Russian = (PInvoke.GetUserDefaultUILanguage() & 0x3FF) == LangRussian;

    public static string DockLeft => Russian ? "Слева" : "Dock left";
    public static string DockRight => Russian ? "Справа" : "Dock right";
    public static string Enabled => Russian ? "Включено" : "Enabled";
    public static string HideHere => Russian ? "Скрыть на этом мониторе" : "Hide on this display";
    public static string Autostart => Russian ? "Автозапуск" : "Start with Windows";
    public static string Exit => Russian ? "Выход" : "Exit";
    public static string Tooltip => Russian ? "montab — панель окон" : "montab — window panel";

    /// <summary>Подпись монитора в меню трея: «Монитор 1 · 2560×1440 (основной)».</summary>
    public static string Monitor(int number, int width, int height, bool primary)
    {
        string label = (Russian ? "Монитор " : "Display ") + number + " · " + width + "×" + height;
        if (primary)
            label += Russian ? " (основной)" : " (primary)";
        return label;
    }
}
