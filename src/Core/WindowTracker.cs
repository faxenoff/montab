using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Montab.Core;

/// <summary>
/// Ведёт список окон верхнего уровня без поллинга: стартовый EnumWindows
/// с alt-tab-фильтром, дальше только WinEvent-хуки (out-of-context —
/// колбэки приходят в наш поток через message loop).
/// </summary>
internal sealed unsafe class WindowTracker : IDisposable
{
    static WindowTracker? s_instance;
    static readonly List<HWND> s_enumScratch = [];

    readonly List<WindowItem> _items = [];
    readonly Dictionary<HWND, WindowItem> _byHwnd = [];
    readonly List<HWINEVENTHOOK> _hooks = [];

    public IReadOnlyList<WindowItem> Items => _items;
    public HWND ForegroundWindow { get; private set; }

    /// <summary>Что-то в списке/состоянии изменилось — нужен relayout+repaint.</summary>
    public event Action? Changed;

    public void Start()
    {
        s_instance = this;

        Hook(PInvoke.EVENT_SYSTEM_FOREGROUND, PInvoke.EVENT_SYSTEM_FOREGROUND);
        Hook(PInvoke.EVENT_SYSTEM_MINIMIZESTART, PInvoke.EVENT_SYSTEM_MINIMIZEEND);
        // 0x8001..0x8003: DESTROY, SHOW, HIDE одним диапазоном
        Hook(PInvoke.EVENT_OBJECT_DESTROY, PInvoke.EVENT_OBJECT_HIDE);
        Hook(PInvoke.EVENT_OBJECT_NAMECHANGE, PInvoke.EVENT_OBJECT_NAMECHANGE);
        // 0x8017..0x8018: CLOAKED, UNCLOAKED (UWP, виртуальные рабочие столы)
        Hook(PInvoke.EVENT_OBJECT_CLOAKED, PInvoke.EVENT_OBJECT_UNCLOAKED);
        // Ресайз источника меняет аспект тайла
        Hook(PInvoke.EVENT_OBJECT_LOCATIONCHANGE, PInvoke.EVENT_OBJECT_LOCATIONCHANGE);

        s_enumScratch.Clear();
        PInvoke.EnumWindows(&EnumWindowsProc, default);
        foreach (var hwnd in s_enumScratch)
        {
            if (IsAppWindow(hwnd))
                _items.Add(CreateItem(hwnd)); // EnumWindows идёт сверху z-order — порядок уже «новые сверху»
        }
        foreach (var item in _items)
            _byHwnd[item.Hwnd] = item;

        ForegroundWindow = PInvoke.GetForegroundWindow();
        Changed?.Invoke();
    }

    void Hook(uint eventMin, uint eventMax)
    {
        var hook = PInvoke.SetWinEventHook(
            eventMin, eventMax, default, &WinEventProc, 0, 0,
            PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);
        if (hook != default)
            _hooks.Add(hook);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static BOOL EnumWindowsProc(HWND hwnd, LPARAM lParam)
    {
        s_enumScratch.Add(hwnd);
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static void WinEventProc(
        HWINEVENTHOOK hook, uint ev, HWND hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            s_instance?.OnWinEvent(ev, hwnd, idObject, idChild);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WinEventProc exception: {ex}");
        }
    }

    void OnWinEvent(uint ev, HWND hwnd, int idObject, int idChild)
    {
        // Только сами окна (OBJID_WINDOW == 0), не дочерние объекты accessibility.
        if (idObject != 0 || idChild != 0 || hwnd == default)
            return;

        switch (ev)
        {
            case PInvoke.EVENT_SYSTEM_FOREGROUND:
                if (ForegroundWindow != hwnd)
                {
                    ForegroundWindow = hwnd;
                    Changed?.Invoke();
                }
                break;

            case PInvoke.EVENT_OBJECT_SHOW:
            case PInvoke.EVENT_OBJECT_UNCLOAKED:
                TryAdd(hwnd);
                break;

            case PInvoke.EVENT_OBJECT_NAMECHANGE:
                if (_byHwnd.TryGetValue(hwnd, out var tracked))
                {
                    string title = GetWindowText(hwnd);
                    if (title.Length > 0 && title != tracked.Title)
                    {
                        tracked.Title = title;
                        Changed?.Invoke();
                    }
                }
                else
                {
                    // Многие приложения ставят заголовок уже после показа окна —
                    // только теперь оно проходит фильтр.
                    TryAdd(hwnd);
                }
                break;

            case PInvoke.EVENT_OBJECT_HIDE:
            case PInvoke.EVENT_OBJECT_DESTROY:
            case PInvoke.EVENT_OBJECT_CLOAKED:
                Remove(hwnd);
                break;

            case PInvoke.EVENT_SYSTEM_MINIMIZESTART:
                SetMinimized(hwnd, true);
                break;

            case PInvoke.EVENT_SYSTEM_MINIMIZEEND:
                SetMinimized(hwnd, false);
                break;

            case PInvoke.EVENT_OBJECT_LOCATIONCHANGE:
                if (_byHwnd.TryGetValue(hwnd, out var moved) && !moved.IsMinimized
                    && UpdateAspect(moved))
                {
                    Changed?.Invoke();
                }
                break;
        }
    }

    void TryAdd(HWND hwnd)
    {
        if (_byHwnd.ContainsKey(hwnd) || !IsAppWindow(hwnd))
            return;

        var item = CreateItem(hwnd);
        _items.Insert(0, item); // новые — сверху
        _byHwnd[hwnd] = item;
        Changed?.Invoke();
    }

    void Remove(HWND hwnd)
    {
        if (!_byHwnd.Remove(hwnd, out var item))
            return;

        _items.Remove(item);
        DestroyOwnedIcon(item);
        Changed?.Invoke();
    }

    void SetMinimized(HWND hwnd, bool minimized)
    {
        if (_byHwnd.TryGetValue(hwnd, out var item) && item.IsMinimized != minimized)
        {
            item.IsMinimized = minimized;
            Changed?.Invoke();
        }
    }

    static WindowItem CreateItem(HWND hwnd)
    {
        var icon = IconLoader.GetWindowIcon(hwnd, out bool owned);
        var item = new WindowItem
        {
            Hwnd = hwnd,
            Title = GetWindowText(hwnd),
            Icon = icon,
            OwnsIcon = owned,
            IsMinimized = PInvoke.IsIconic(hwnd),
        };
        UpdateAspect(item);
        return item;
    }

    /// <summary>true — аспект источника ощутимо изменился.</summary>
    static bool UpdateAspect(WindowItem item)
    {
        if (!PInvoke.GetClientRect(item.Hwnd, out RECT rc))
            return false;
        int w = rc.right - rc.left, h = rc.bottom - rc.top;
        if (w <= 0 || h <= 0)
            return false;

        double aspect = (double)w / h;
        if (Math.Abs(aspect - item.Aspect) < 0.01)
            return false;

        item.Aspect = aspect;
        return true;
    }

    /// <summary>Классический alt-tab-фильтр + отсев cloaked-окон.</summary>
    static bool IsAppWindow(HWND hwnd)
    {
        if (!PInvoke.IsWindowVisible(hwnd))
            return false;
        if (PInvoke.GetWindowTextLength(hwnd) == 0)
            return false;

        var exStyle = (WINDOW_EX_STYLE)unchecked((uint)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE));
        if ((exStyle & WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) != 0 && (exStyle & WINDOW_EX_STYLE.WS_EX_APPWINDOW) == 0)
            return false;

        // Правило alt-tab (Raymond Chen): у owned-цепочки показывается корневой владелец.
        if ((exStyle & WINDOW_EX_STYLE.WS_EX_APPWINDOW) == 0)
        {
            HWND walk = default;
            HWND probe = PInvoke.GetAncestor(hwnd, GET_ANCESTOR_FLAGS.GA_ROOTOWNER);
            while (probe != walk)
            {
                walk = probe;
                probe = PInvoke.GetLastActivePopup(walk);
                if (PInvoke.IsWindowVisible(probe))
                    break;
            }
            if (walk != hwnd)
                return false;
        }

        // Cloaked: suspended UWP, окна других виртуальных рабочих столов.
        int cloaked = 0;
        PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(int));
        return cloaked == 0;
    }

    static string GetWindowText(HWND hwnd)
    {
        Span<char> buffer = stackalloc char[512];
        fixed (char* p = buffer)
        {
            int length = PInvoke.GetWindowText(hwnd, p, buffer.Length);
            return length > 0 ? new string(buffer[..length]) : "";
        }
    }

    static void DestroyOwnedIcon(WindowItem item)
    {
        if (item.OwnsIcon && item.Icon != default)
        {
            PInvoke.DestroyIcon(item.Icon);
            item.Icon = default;
        }
    }

    public void Dispose()
    {
        foreach (var hook in _hooks)
            PInvoke.UnhookWinEvent(hook);
        _hooks.Clear();

        foreach (var item in _items)
            DestroyOwnedIcon(item);

        if (s_instance == this)
            s_instance = null;
    }
}
