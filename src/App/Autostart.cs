namespace Montab.App;

/// <summary>Автозапуск через классический ключ Run текущего пользователя.</summary>
internal static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "montab";

    public static bool IsEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Toggle()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (key.GetValue(ValueName) is string)
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        else
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
    }
}
