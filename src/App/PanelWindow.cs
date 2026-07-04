using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Montab.Config;
using Montab.Core;
using Montab.Thumbs;
using Montab.UI;
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

    const nuint ActivateTimerId = 1;
    const nint MK_CONTROL = 0x0008;

    static PanelWindow? s_instance;
    static readonly List<HMONITOR> s_monitorScratch = [];
    static readonly HWND TopmostAnchor = new(-1); // HWND_TOPMOST

    readonly Settings _settings;
    readonly WindowTracker _tracker = new();
    readonly SwitchController _switch = new();
    readonly LayoutEngine _layout = new();
    readonly Renderer _renderer = new();
    List<LayoutItem> _layoutItems = [];
    int _scrollOffset;

    HWND _hwnd;
    AppBar? _appBar;
    ThumbnailManager? _thumbs;
    uint _dpi = 96;
    bool _updatingPosition;
    bool _resizing;
    bool _swallowNextUp;
    HWND _pendingActivate;

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
                style = WNDCLASS_STYLES.CS_HREDRAW | WNDCLASS_STYLES.CS_VREDRAW | WNDCLASS_STYLES.CS_DBLCLKS,
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

        _thumbs = new ThumbnailManager(_hwnd);
        _tracker.Changed += OnTrackerChanged;
        _tracker.ForegroundChanged += _switch.OnForegroundChanged;
        _tracker.Start();
        _switch.OnForegroundChanged(_tracker.ForegroundWindow);
    }

    void OnTrackerChanged() => PInvoke.InvalidateRect(_hwnd, null, false);

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
            case PInvoke.WM_PAINT:
                PInvoke.GetClientRect(hwnd, out RECT client);
                _layoutItems = _layout.Compute(_tracker.Items, client, _dpi, _scrollOffset);
                int maxScroll = Math.Max(0, _layout.TotalHeight - (client.bottom - client.top));
                if (_scrollOffset > maxScroll)
                {
                    _scrollOffset = maxScroll;
                    _layoutItems = _layout.Compute(_tracker.Items, client, _dpi, _scrollOffset);
                }
                _thumbs?.Sync(_layoutItems, client, _tracker.ForegroundWindow);
                _renderer.Paint(hwnd, _layoutItems, _tracker.ForegroundWindow, _dpi);
                return new LRESULT(0);

            case PInvoke.WM_MOUSEWHEEL:
                int delta = (short)((wParam.Value >> 16) & 0xFFFF);
                if (((nint)wParam.Value & MK_CONTROL) != 0)
                    CtrlZoom(delta);
                else
                    Scroll(-delta);
                return new LRESULT(0);

            case PInvoke.WM_ERASEBKGND:
                return new LRESULT(1); // всё рисуется в WM_PAINT с двойным буфером

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
                if (((nint)wParam.Value & MK_CONTROL) != 0)
                    CtrlPan(GetXLParam(lParam), GetYLParam(lParam));
                break;

            case PInvoke.WM_LBUTTONUP:
                if (_swallowNextUp)
                {
                    // Финальный UP последовательности двойного клика — не одиночный клик.
                    _swallowNextUp = false;
                }
                else if (_resizing)
                {
                    _resizing = false;
                    PInvoke.ReleaseCapture();
                    _settings.Save();
                }
                else
                {
                    OnClick(GetXLParam(lParam), GetYLParam(lParam), ((nint)wParam.Value & MK_CONTROL) != 0);
                }
                return new LRESULT(0);

            case PInvoke.WM_LBUTTONDBLCLK:
                _swallowNextUp = true;
                OnDoubleClick(GetXLParam(lParam), GetYLParam(lParam));
                return new LRESULT(0);

            case PInvoke.WM_TIMER:
                if (wParam.Value == ActivateTimerId)
                {
                    PInvoke.KillTimer(hwnd, ActivateTimerId);
                    if (_pendingActivate != default)
                    {
                        _switch.Activate(_pendingActivate);
                        _pendingActivate = default;
                    }
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
                _tracker.Dispose();
                _thumbs?.Dispose();
                _renderer.Dispose();
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
            // MoveWindow недостаточно: бит WS_EX_TOPMOST может рассинхронизироваться
            // с фактической z-позицией — переутверждаем topmost при каждом размещении.
            PInvoke.SetWindowPos(_hwnd, TopmostAnchor,
                rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
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

    void Scroll(int wheelDelta)
    {
        int step = LayoutEngine.Scale(60, _dpi); // px за один щелчок колеса
        int offset = _scrollOffset + wheelDelta * step / 120;

        PInvoke.GetClientRect(_hwnd, out RECT client);
        int maxScroll = Math.Max(0, _layout.TotalHeight - (client.bottom - client.top));
        offset = Math.Clamp(offset, 0, maxScroll);

        if (offset == _scrollOffset)
            return;
        _scrollOffset = offset;
        PInvoke.InvalidateRect(_hwnd, null, false);
    }

    static int GetXLParam(LPARAM lParam) => (short)(lParam.Value & 0xFFFF);

    static int GetYLParam(LPARAM lParam) => (short)((lParam.Value >> 16) & 0xFFFF);

    static bool Inside(RECT r, int x, int y) => x >= r.left && x < r.right && y >= r.top && y < r.bottom;

    LayoutItem? HitTest(int x, int y)
    {
        foreach (var li in _layoutItems)
        {
            if (Inside(li.Bounds, x, y))
                return li;
        }
        return null;
    }

    void OnClick(int x, int y, bool ctrl)
    {
        if (HitTest(x, y) is not { } li)
            return;

        if (ctrl)
        {
            // Ctrl+клик — сброс zoom&pan этого превью
            li.Window.ResetZoom();
            PInvoke.InvalidateRect(_hwnd, null, false);
            return;
        }

        if (!li.IsStrip && Inside(li.Preview, x, y))
        {
            _switch.Activate(li.Window.Hwnd);
            return;
        }

        // Клик по заголовку/полоске активируем с задержкой,
        // чтобы двойной клик мог его отменить (dblclick = свернуть/развернуть).
        _pendingActivate = li.Window.Hwnd;
        PInvoke.SetTimer(_hwnd, ActivateTimerId, PInvoke.GetDoubleClickTime(), null);
    }

    void OnDoubleClick(int x, int y)
    {
        PInvoke.KillTimer(_hwnd, ActivateTimerId);
        _pendingActivate = default;

        if (HitTest(x, y) is not { } li)
            return;

        if (li.IsStrip || Inside(li.Label, x, y))
        {
            li.Window.IsCollapsed = !li.Window.IsCollapsed;
            PInvoke.InvalidateRect(_hwnd, null, false);
        }
    }

    /// <summary>Ctrl+колесо над превью: постоянный zoom ×1..×5.</summary>
    void CtrlZoom(int wheelDelta)
    {
        var pt = GetCursorClientPos();
        if (HitTest(pt.X, pt.Y) is not { IsStrip: false } li || !Inside(li.Preview, pt.X, pt.Y))
            return;

        var item = li.Window;
        double zoom = Math.Clamp(item.Zoom * (wheelDelta > 0 ? 1.25 : 0.8), 1.0, 5.0);
        if (zoom < 1.05)
        {
            item.ResetZoom();
        }
        else
        {
            item.Zoom = zoom;
        }
        PInvoke.InvalidateRect(_hwnd, null, false);
    }

    /// <summary>Ctrl+движение мыши над увеличенным превью: центр видимой области.</summary>
    void CtrlPan(int x, int y)
    {
        if (HitTest(x, y) is not { IsStrip: false } li || li.Window.Zoom <= 1.001)
            return;

        var fit = LayoutEngine.FitRect(li.Preview, li.Window.Aspect);
        int w = fit.right - fit.left, h = fit.bottom - fit.top;
        if (w <= 0 || h <= 0)
            return;

        li.Window.CenterX = Math.Clamp((x - fit.left) / (double)w, 0, 1);
        li.Window.CenterY = Math.Clamp((y - fit.top) / (double)h, 0, 1);
        PInvoke.InvalidateRect(_hwnd, null, false);
    }

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
