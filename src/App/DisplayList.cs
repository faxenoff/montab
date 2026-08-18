using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Montab.App;

/// <summary>Монитор системы на момент перечисления.</summary>
internal readonly record struct DisplayInfo(HMONITOR Handle, string Device, RECT Rect, bool Primary)
{
    public int Width => Rect.right - Rect.left;
    public int Height => Rect.bottom - Rect.top;
}

/// <summary>Перечисление мониторов: EnumDisplayMonitors + MONITORINFOEXW.</summary>
internal static unsafe class DisplayList
{
    static readonly List<HMONITOR> s_scratch = [];

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static BOOL EnumProc(HMONITOR monitor, HDC hdc, RECT* clip, LPARAM lParam)
    {
        s_scratch.Add(monitor);
        return true;
    }

    /// <summary>Заполняет список мониторами слева направо (порядок пунктов меню).</summary>
    public static void Enumerate(List<DisplayInfo> result)
    {
        s_scratch.Clear();
        PInvoke.EnumDisplayMonitors(default, null, &EnumProc, default);

        result.Clear();
        foreach (var handle in s_scratch)
        {
            MONITORINFOEXW mi = default;
            mi.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
            if (!PInvoke.GetMonitorInfo(handle, (MONITORINFO*)&mi))
                continue;

            result.Add(new DisplayInfo(
                handle,
                mi.szDevice.ToString(),
                mi.monitorInfo.rcMonitor,
                (mi.monitorInfo.dwFlags & PInvoke.MONITORINFOF_PRIMARY) != 0));
        }

        result.Sort(static (a, b) => a.Rect.left != b.Rect.left ? a.Rect.left - b.Rect.left : a.Rect.top - b.Rect.top);
    }
}
