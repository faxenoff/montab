using Montab.Config;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace Montab.App;

/// <summary>
/// Обёртка протокола AppBar (SHAppBarMessage): регистрация панели у shell,
/// резервирование края рабочей области, согласование позиции.
/// </summary>
internal sealed unsafe class AppBar
{
    readonly HWND _hwnd;
    bool _registered;

    public uint CallbackMessage { get; }

    public AppBar(HWND hwnd, uint callbackMessage)
    {
        _hwnd = hwnd;
        CallbackMessage = callbackMessage;
    }

    APPBARDATA NewData() => new()
    {
        cbSize = (uint)sizeof(APPBARDATA),
        hWnd = _hwnd,
        uCallbackMessage = CallbackMessage,
    };

    public void Register()
    {
        if (_registered)
            return;
        var abd = NewData();
        PInvoke.SHAppBarMessage(PInvoke.ABM_NEW, ref abd);
        _registered = true;
    }

    public void Unregister()
    {
        if (!_registered)
            return;
        var abd = NewData();
        PInvoke.SHAppBarMessage(PInvoke.ABM_REMOVE, ref abd);
        _registered = false;
    }

    /// <summary>
    /// Согласовывает с shell полосу заданной ширины у левого/правого края монитора
    /// и возвращает итоговый прямоугольник, в который нужно поставить окно.
    /// </summary>
    public RECT SetPos(DockEdge edge, RECT monitor, int width)
    {
        var abd = NewData();
        abd.uEdge = edge == DockEdge.Left ? PInvoke.ABE_LEFT : PInvoke.ABE_RIGHT;
        abd.rc = monitor;
        ImposeWidth(ref abd.rc, edge, width);

        PInvoke.SHAppBarMessage(PInvoke.ABM_QUERYPOS, ref abd);
        ImposeWidth(ref abd.rc, edge, width);

        PInvoke.SHAppBarMessage(PInvoke.ABM_SETPOS, ref abd);
        return abd.rc;
    }

    static void ImposeWidth(ref RECT rc, DockEdge edge, int width)
    {
        if (edge == DockEdge.Left)
            rc.right = rc.left + width;
        else
            rc.left = rc.right - width;
    }
}
