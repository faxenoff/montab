using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Registry;

namespace Montab.App;

/// <summary>Автозапуск через классический ключ Run текущего пользователя.</summary>
internal static unsafe class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "montab";

    public static bool IsEnabled()
    {
        fixed (char* key = RunKey, name = ValueName)
        {
            // Нужен только факт: строковое значение есть — буфер под данные не передаётся
            return PInvoke.RegGetValue(
                HKEY.HKEY_CURRENT_USER, key, name,
                REG_ROUTINE_FLAGS.RRF_RT_REG_SZ | REG_ROUTINE_FLAGS.RRF_RT_REG_EXPAND_SZ | REG_ROUTINE_FLAGS.RRF_NOEXPAND,
                null, null, null) == WIN32_ERROR.ERROR_SUCCESS;
        }
    }

    public static void Toggle()
    {
        bool enabled = IsEnabled();
        string command = $"\"{Environment.ProcessPath}\"";
        fixed (char* key = RunKey, name = ValueName, data = command)
        {
            if (enabled)
                PInvoke.RegDeleteKeyValue(HKEY.HKEY_CURRENT_USER, key, name);
            else
                PInvoke.RegSetKeyValue(
                    HKEY.HKEY_CURRENT_USER, key, name, (uint)REG_VALUE_TYPE.REG_SZ,
                    data, (uint)((command.Length + 1) * sizeof(char)));
        }
    }
}
