using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Montab.App;

/// <summary>
/// Классическая связка «HWND → управляющий объект» через GWLP_USERDATA:
/// указатель кладётся при WM_NCCREATE и освобождается при WM_NCDESTROY.
/// Панелей и оверлеев теперь много, статический синглтон не годится.
/// </summary>
internal static unsafe class WindowRef
{
    /// <summary>Значение lpParam для CreateWindowEx.</summary>
    public static void* Pin(object target) => (void*)GCHandle.ToIntPtr(GCHandle.Alloc(target));

    /// <summary>WM_NCCREATE: перенести переданный lpParam в GWLP_USERDATA окна.</summary>
    public static void Bind(HWND hwnd, LPARAM lParam)
    {
        var cs = (CREATESTRUCTW*)lParam.Value;
        PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWLP_USERDATA, (nint)cs->lpCreateParams);
    }

    /// <summary>Объект окна или null, если оно ещё не привязано (сообщения до WM_NCCREATE).</summary>
    public static object? Get(HWND hwnd)
    {
        nint state = PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWLP_USERDATA);
        return state == 0 ? null : GCHandle.FromIntPtr(state).Target;
    }

    /// <summary>WM_NCDESTROY: отпустить объект — окна уже нет.</summary>
    public static void Release(HWND hwnd)
    {
        nint state = PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWLP_USERDATA);
        if (state == 0)
            return;
        PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWLP_USERDATA, 0);
        GCHandle.FromIntPtr(state).Free();
    }
}
