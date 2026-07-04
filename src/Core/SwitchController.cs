using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Montab.Core;

/// <summary>
/// Переключение foreground-окон с MRU-историей активации: клик по уже
/// активному окну (и фокус после скрытия) идёт в последнее по истории
/// открытое окно; скрытые в полоску исключаются фильтром.
/// </summary>
internal sealed class SwitchController
{
    const byte VkMenu = 0x12; // VK_MENU (Alt)
    const int MaxHistory = 32;

    readonly List<HWND> _history = []; // голова — самое свежее foreground-окно

    /// <summary>Фильтр целей автоперехода (панель исключает полоски: свёрнутые/погашенные).</summary>
    public Func<HWND, bool>? IsEligibleTarget { get; set; }

    public void OnForegroundChanged(HWND hwnd)
    {
        if (hwnd == default)
            return;
        _history.Remove(hwnd);
        _history.Insert(0, hwnd);
        if (_history.Count > MaxHistory)
            _history.RemoveAt(_history.Count - 1);
    }

    public void Activate(HWND target) => ActivateCore(target);

    /// <summary>
    /// Активирует последнее по истории открытое окно, пропуская указанное,
    /// закрытые окна и всё, что не проходит фильтр (скрытые полоски).
    /// </summary>
    public void ActivateMostRecentExcept(HWND except)
    {
        foreach (var hwnd in _history)
        {
            if (hwnd == except || !PInvoke.IsWindow(hwnd))
                continue;
            if (IsEligibleTarget is { } eligible && !eligible(hwnd))
                continue;
            ActivateCore(hwnd);
            return;
        }
    }

    void ActivateCore(HWND goal)
    {
        if (PInvoke.IsIconic(goal))
            PInvoke.ShowWindow(goal, SHOW_WINDOW_CMD.SW_RESTORE);

        if (!PInvoke.SetForegroundWindow(goal))
        {
            // Панель не активируется (WS_EX_NOACTIVATE), поэтому система может
            // держать foreground lock. Имитация нажатия Alt его снимает.
            PInvoke.keybd_event(VkMenu, 0, 0, 0);
            PInvoke.keybd_event(VkMenu, 0, KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP, 0);
            PInvoke.SetForegroundWindow(goal);
        }
    }
}
