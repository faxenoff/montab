using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Montab.Config;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Montab.App;

/// <summary>
/// Главное окно панели: WS_POPUP без рамки, докается через AppBar,
/// не активируется по клику (WS_EX_NOACTIVATE), скрыто из alt-tab.
/// </summary>
internal sealed unsafe class PanelWindow
{
    const string ClassName = "montab.panel";
    const int GripLogicalPx = 6;

    const uint CmdDockLeft = 1;
    const uint CmdDockRight = 2;
    const uint CmdExit = 3;

    static PanelWindow? s_instance;
    static readonly List<HMONITOR> s_monitorScratch = [];

    readonly Settings _settings;
    HWND _hwnd;
    AppBar? _appBar;
    uint _dpi = 96;
    bool _updatingPosition;
    bool _resizing;

    public PanelWindow(Settings settings) => _settings = settings;

    public HWND Handle => _hwnd;

    public void Create()
    {
        s_instance = this;
        var hInstance = PInvoke.GetModuleHandle(null);

        fixed (char* className = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = WNDCLASS_STYLES.CS_HREDRAW | WNDCLASS_STYLES.CS_VREDRAW,
                lpfnWndProc = &StaticWndProc,
                hInstance = (HINSTANCE)hInstance.Value,
                hCursor = PInvoke.LoadCursor(default, PInvoke.IDC_ARROW),
                hbrBackground = PInvoke.CreateSolidBrush(new COLORREF(0x001E1E1E)),
                lpszClassName = className,
            };
            if (PInvoke.RegisterClassEx(&wc) == 0)
                throw new InvalidOperationException("RegisterClassEx failed");
        }

        _hwnd = PInvoke.CreateWindowEx(
            WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOPMOST,
            ClassName,
            "montab",
            WINDOW_STYLE.WS_POPUP,
            0, 0, 200, 200,
            default, default, (HINSTANCE)hInstance.Value, null);

        if (_hwnd == default)
            throw new InvalidOperationException("CreateWindowExW failed");

        _dpi = PInvoke.GetDpiForWindow(_hwnd);
        _appBar = new AppBar(_hwnd, PInvoke.RegisterWindowMessage("montab.appbar"));
        _appBar.Register();
        UpdatePosition();
        PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static LRESULT StaticWndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        // Исключение, ушедшее в нативный фрейм, под AOT роняет процесс без диагностики.
        try
        {
            return s_instance?.HandleMessage(hwnd, msg, wParam, lParam)
                ?? PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WndProc exception: {ex}");
            return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    LRESULT HandleMessage(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        if (_appBar is not null && msg == _appBar.CallbackMessage)
        {
            if ((uint)wParam.Value == PInvoke.ABN_POSCHANGED)
                UpdatePosition();
            return new LRESULT(0);
        }

        switch (msg)
        {
            case PInvoke.WM_SETCURSOR:
                if ((lParam.Value & 0xFFFF) == PInvoke.HTCLIENT && IsInGrip(GetCursorClientPos().X))
                {
                    PInvoke.SetCursor(PInvoke.LoadCursor(default, PInvoke.IDC_SIZEWE));
                    return new LRESULT(1);
                }
                break;

            case PInvoke.WM_LBUTTONDOWN:
                if (IsInGrip(GetXLParam(lParam)))
                {
                    _resizing = true;
                    PInvoke.SetCapture(hwnd);
                    return new LRESULT(0);
                }
                break;

            case PInvoke.WM_MOUSEMOVE:
                if (_resizing)
                {
                    PInvoke.GetCursorPos(out System.Drawing.Point screen);
                    ResizeToScreenX(screen.X);
                    return new LRESULT(0);
                }
                break;

            case PInvoke.WM_LBUTTONUP:
                if (_resizing)
                {
                    _resizing = false;
                    PInvoke.ReleaseCapture();
                    _settings.Save();
                    return new LRESULT(0);
                }
                break;

            case PInvoke.WM_CAPTURECHANGED:
                _resizing = false;
                break;

            case PInvoke.WM_RBUTTONUP:
                ShowContextMenu();
                return new LRESULT(0);

            case PInvoke.WM_DPICHANGED:
                _dpi = (uint)(wParam.Value & 0xFFFF);
                UpdatePosition();
                return new LRESULT(0);

            case PInvoke.WM_DISPLAYCHANGE:
                UpdatePosition();
                break;

            case PInvoke.WM_CLOSE:
                PInvoke.DestroyWindow(hwnd);
                return new LRESULT(0);

            case PInvoke.WM_DESTROY:
                _appBar?.Unregister();
                _settings.Save();
                PInvoke.PostQuitMessage(0);
                return new LRESULT(0);
        }

        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    #region Позиционирование

    /// <summary>Пересогласовывает полосу appbar'а и ставит окно в полученный rect.</summary>
    void UpdatePosition()
    {
        if (_updatingPosition || _appBar is null)
            return;
        _updatingPosition = true;
        try
        {
            var (monitor, device) = GetTargetMonitor();
            _settings.Monitor = device;

            int monitorWidth = monitor.right - monitor.left;
            double pct = Math.Clamp(_settings.WidthPercent, Settings.MinWidthPercent, Settings.MaxWidthPercent);
            int width = Math.Max(40, (int)Math.Round(monitorWidth * pct / 100.0));

            var rc = _appBar.SetPos(_settings.Edge, monitor, width);
            PInvoke.MoveWindow(_hwnd, rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top, true);
            _dpi = PInvoke.GetDpiForWindow(_hwnd);
        }
        finally
        {
            _updatingPosition = false;
        }
    }

    void ResizeToScreenX(int screenX)
    {
        var (monitor, _) = GetTargetMonitor();
        int monitorWidth = monitor.right - monitor.left;
        if (monitorWidth <= 0)
            return;

        int widthPx = _settings.Edge == DockEdge.Left
            ? screenX - monitor.left
            : monitor.right - screenX;

        double pct = Math.Clamp(widthPx * 100.0 / monitorWidth, Settings.MinWidthPercent, Settings.MaxWidthPercent);
        if (Math.Abs(pct - _settings.WidthPercent) < 0.05)
            return;

        _settings.WidthPercent = pct;
        UpdatePosition();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static BOOL EnumMonitorProc(HMONITOR hMonitor, HDC hdc, RECT* rect, LPARAM lParam)
    {
        s_monitorScratch.Add(hMonitor);
        return true;
    }

    /// <summary>Монитор из настроек, иначе primary, иначе первый попавшийся.</summary>
    (RECT Rect, string Device) GetTargetMonitor()
    {
        s_monitorScratch.Clear();
        PInvoke.EnumDisplayMonitors(default, null, &EnumMonitorProc, default);

        RECT primaryRect = default;
        string primaryDevice = "";
        bool primaryFound = false;

        foreach (var hMonitor in s_monitorScratch)
        {
            MONITORINFOEXW mi = default;
            mi.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
            if (!PInvoke.GetMonitorInfo(hMonitor, (MONITORINFO*)&mi))
                continue;

            string device = mi.szDevice.ToString();
            if (device == _settings.Monitor)
                return (mi.monitorInfo.rcMonitor, device);

            if (!primaryFound && (mi.monitorInfo.dwFlags & PInvoke.MONITORINFOF_PRIMARY) != 0)
            {
                primaryRect = mi.monitorInfo.rcMonitor;
                primaryDevice = device;
                primaryFound = true;
            }
        }

        if (primaryFound)
            return (primaryRect, primaryDevice);

        // Fallback: рабочий стол хоть где-то есть.
        var fallback = new RECT { left = 0, top = 0, right = 1920, bottom = 1080 };
        return (fallback, "");
    }

    #endregion

    #region Взаимодействие

    int Scale(int logicalPx) => (int)(logicalPx * _dpi / 96.0);

    static int GetXLParam(LPARAM lParam) => (short)(lParam.Value & 0xFFFF);

    System.Drawing.Point GetCursorClientPos()
    {
        PInvoke.GetCursorPos(out System.Drawing.Point pt);
        PInvoke.ScreenToClient(_hwnd, ref pt);
        return pt;
    }

    /// <summary>Зона захвата ресайза — узкая полоса вдоль внутреннего края панели.</summary>
    bool IsInGrip(int clientX)
    {
        PInvoke.GetClientRect(_hwnd, out RECT rc);
        int grip = Scale(GripLogicalPx);
        return _settings.Edge == DockEdge.Left
            ? clientX >= rc.right - grip
            : clientX <= grip;
    }

    void ShowContextMenu()
    {
        var menu = PInvoke.CreatePopupMenu();
        if (menu == default)
            return;

        try
        {
            var left = MENU_ITEM_FLAGS.MF_STRING | (_settings.Edge == DockEdge.Left ? MENU_ITEM_FLAGS.MF_CHECKED : 0);
            var right = MENU_ITEM_FLAGS.MF_STRING | (_settings.Edge == DockEdge.Right ? MENU_ITEM_FLAGS.MF_CHECKED : 0);
            PInvoke.AppendMenu(menu, left, CmdDockLeft, "Слева");
            PInvoke.AppendMenu(menu, right, CmdDockRight, "Справа");
            PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, null);
            PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, CmdExit, "Выход");

            PInvoke.GetCursorPos(out System.Drawing.Point pt);
            // Классический трюк: без этого меню у неактивируемого окна не закрывается кликом мимо.
            PInvoke.SetForegroundWindow(_hwnd);
            var cmd = PInvoke.TrackPopupMenu(
                menu,
                TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON,
                pt.X, pt.Y, _hwnd, null);

            switch ((uint)cmd.Value)
            {
                case CmdDockLeft:
                    SetEdge(DockEdge.Left);
                    break;
                case CmdDockRight:
                    SetEdge(DockEdge.Right);
                    break;
                case CmdExit:
                    PInvoke.DestroyWindow(_hwnd);
                    break;
            }
        }
        finally
        {
            PInvoke.DestroyMenu(menu);
        }
    }

    void SetEdge(DockEdge edge)
    {
        if (_settings.Edge == edge)
            return;
        _settings.Edge = edge;
        UpdatePosition();
        _settings.Save();
    }

    #endregion
}
