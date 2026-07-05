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
using Windows.Win32.UI.Input.KeyboardAndMouse;
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
    const uint CmdAutostart = 4;

    const string AutostartRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AutostartValue = "montab";

    const nuint ActivateTimerId = 1;
    const nuint HoverZoomTimerId = 2;
    /// <summary>Окно ожидания второго клика по живому тайлу.</summary>
    const uint LabelActivateDelayMs = 150;
    const uint HoverZoomDelayMs = 700;
    const double HoverZoomFactor = 5.0;
    const nint MK_CONTROL = 0x0008;

    enum PressState { None, Pressed, Dragging, PanelDrag }

    static PanelWindow? s_instance;
    static readonly List<HMONITOR> s_monitorScratch = [];
    static readonly HWND TopmostAnchor = new(-1); // HWND_TOPMOST
    static readonly HWND BottomAnchor = new(1);   // HWND_BOTTOM

    // Системные курсоры разделяемые — загружаются один раз, не освобождаются
    static readonly HCURSOR ArrowCursor = PInvoke.LoadCursor(default, PInvoke.IDC_ARROW);
    static readonly HCURSOR SizeWECursor = PInvoke.LoadCursor(default, PInvoke.IDC_SIZEWE);
    static readonly HCURSOR SizeNSCursor = PInvoke.LoadCursor(default, PInvoke.IDC_SIZENS);
    static readonly HCURSOR SizeAllCursor = PInvoke.LoadCursor(default, PInvoke.IDC_SIZEALL);

    readonly Settings _settings;
    readonly WindowTracker _tracker = new();
    readonly SwitchController _switch = new();
    readonly LayoutEngine _layout = new();
    readonly Renderer _renderer = new();
    IReadOnlyList<LayoutItem> _layoutItems = [];
    int _scrollOffset;

    HWND _hwnd;
    AppBar? _appBar;
    ThumbnailManager? _thumbs;
    ScrollBarWindow? _scrollbar;
    bool _pointerNearby;
    bool _updatingPosition;
    bool _resizing;

    // Предвычисленные размеры на текущий DPI
    uint _dpi = 96;
    int _gripPx, _headerPx, _dragThresholdPx, _repeatRadiusPx, _wheelStepPx;

    // Двойной клик детектируем сами: системный WM_LBUTTONDBLCLK ненадёжен,
    // когда лента перестраивается после первого клика.
    WindowItem? _pendingLabelItem; // живой тайл ждёт активации по таймеру
    int _closeClickTick;
    int _closeClickX, _closeClickY;

    PressState _press;
    WindowItem? _pressItem;
    WindowItem? _hoverClose;
    bool _swallowNextUp;
    int _pressX, _pressY;

    // Hover-лупа: наведение на превью без нажатий включает временный zoom+pan
    WindowItem? _hoverZoomItem;
    WindowItem? _hoverCandidate;
    double _savedZoom = 1, _savedCenterX = 0.5, _savedCenterY = 0.5;

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
                hCursor = ArrowCursor,
                // hbrBackground не нужен: WM_ERASEBKGND подавлен, весь фон рисует Renderer
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

        SetDpi(PInvoke.GetDpiForWindow(_hwnd));
        _scrollbar = new ScrollBarWindow(this, _hwnd, (HINSTANCE)hInstance.Value);
        _appBar = new AppBar(_hwnd, PInvoke.RegisterWindowMessage("montab.appbar"));
        _appBar.Register();
        UpdatePosition();
        PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);

        _thumbs = new ThumbnailManager(_hwnd);
        _tracker.Changed += OnTrackerChanged;
        _tracker.ForegroundChanged += _switch.OnForegroundChanged;
        // Автопереходы по истории не идут в свёрнутые окна
        _switch.IsEligibleTarget = hwnd => _tracker.TryGet(hwnd, out var item) && !item.IsMinimized;
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
            switch ((uint)wParam.Value)
            {
                case PInvoke.ABN_POSCHANGED:
                    UpdatePosition();
                    break;
                case PInvoke.ABN_FULLSCREENAPP:
                    // Полноэкранное приложение: уходим вниз z-order, потом возвращаемся.
                    bool fullscreen = lParam.Value != 0;
                    PInvoke.SetWindowPos(_hwnd, fullscreen ? BottomAnchor : TopmostAnchor,
                        0, 0, 0, 0,
                        SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                        SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
                    break;
            }
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
                _renderer.Paint(hwnd, _layoutItems, _tracker.ForegroundWindow, _dpi, _hoverClose,
                    _press == PressState.Dragging ? _pressItem : null);
                _scrollbar?.Update(_layout.TotalHeight, client.bottom - client.top, _scrollOffset, _pointerNearby);
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
                if ((lParam.Value & 0xFFFF) == PInvoke.HTCLIENT)
                {
                    var cur = GetCursorClientPos();
                    if (IsInGrip(cur.X))
                    {
                        PInvoke.SetCursor(SizeWECursor);
                        return new LRESULT(1);
                    }
                    if (cur.Y < _headerPx)
                    {
                        PInvoke.SetCursor(SizeAllCursor);
                        return new LRESULT(1);
                    }
                    // Активная hover-лупа или Ctrl над увеличенным превью — режим pan
                    bool ctrlHeld = (PInvoke.GetKeyState(0x11 /* VK_CONTROL */) & 0x8000) != 0;
                    if (HitTest(cur.X, cur.Y) is { IsStrip: false } hover
                        && Inside(hover.Preview, cur.X, cur.Y)
                        && (hover.Window == _hoverZoomItem || (ctrlHeld && hover.Window.Zoom > 1.001)))
                    {
                        PInvoke.SetCursor(SizeAllCursor);
                        return new LRESULT(1);
                    }
                }
                break;

            case PInvoke.WM_LBUTTONDOWN:
                if (IsInGrip(GetXLParam(lParam)))
                {
                    _resizing = true;
                    PInvoke.SetCapture(hwnd);
                    return new LRESULT(0);
                }
                OnPress(GetXLParam(lParam), GetYLParam(lParam), ((nint)wParam.Value & MK_CONTROL) != 0);
                return new LRESULT(0);

            case PInvoke.WM_MOUSEMOVE:
                PointerSeen();
                if (_resizing)
                {
                    PInvoke.GetCursorPos(out System.Drawing.Point screen);
                    ResizeToScreenX(screen.X);
                    return new LRESULT(0);
                }
                OnMove(GetXLParam(lParam), GetYLParam(lParam), ((nint)wParam.Value & MK_CONTROL) != 0);
                break;

            case PInvoke.WM_LBUTTONUP:
                if (_swallowNextUp)
                {
                    // Отпускание второго клика двойного — жест уже обработан на DOWN
                    _swallowNextUp = false;
                    EndPress();
                    return new LRESULT(0);
                }
                if (_resizing)
                {
                    _resizing = false;
                    PInvoke.ReleaseCapture();
                    _settings.Save();
                }
                else if (_press == PressState.Dragging)
                {
                    EndPress();
                }
                else if (_press == PressState.PanelDrag)
                {
                    EndPress();
                    DropPanel();
                }
                else
                {
                    EndPress();
                    OnClick(GetXLParam(lParam), GetYLParam(lParam), ((nint)wParam.Value & MK_CONTROL) != 0);
                }
                return new LRESULT(0);

            case PInvoke.WM_TIMER:
                if (wParam.Value == ActivateTimerId)
                {
                    PInvoke.KillTimer(hwnd, ActivateTimerId);
                    if (_pendingLabelItem is { } pending)
                    {
                        _pendingLabelItem = null;
                        if (PInvoke.IsWindow(pending.Hwnd))
                            _switch.Activate(pending.Hwnd);
                    }
                }
                else if (wParam.Value == HoverZoomTimerId)
                {
                    PInvoke.KillTimer(hwnd, HoverZoomTimerId);
                    TryBeginHoverZoom();
                }
                break;

            case PInvoke.WM_MOUSELEAVE:
                CancelHoverZoom();
                PointerMaybeGone();
                if (_hoverClose is not null)
                {
                    _hoverClose = null;
                    PInvoke.InvalidateRect(hwnd, null, false);
                }
                break;

            case PInvoke.WM_CAPTURECHANGED:
                _resizing = false;
                _press = PressState.None;
                _pressItem = null;
                break;

            case PInvoke.WM_RBUTTONUP:
                ShowContextMenu();
                return new LRESULT(0);

            case PInvoke.WM_DPICHANGED:
                SetDpi((uint)(wParam.Value & 0xFFFF));
                UpdatePosition();
                return new LRESULT(0);

            case PInvoke.WM_DISPLAYCHANGE:
                UpdatePosition();
                break;

            case PInvoke.WM_ENDSESSION:
                // Выключение/перезагрузка Windows: WM_DESTROY может не прийти
                if (wParam.Value != 0)
                    _settings.Save();
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
            SetDpi(PInvoke.GetDpiForWindow(_hwnd));
            _scrollbar?.Layout(rc, _dpi, _settings.Edge == DockEdge.Right, _headerPx);
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

    /// <summary>Пересчёт кеша DPI-зависимых размеров (вызывается при смене DPI).</summary>
    void SetDpi(uint dpi)
    {
        if (dpi == _dpi && _gripPx != 0)
            return;
        _dpi = dpi;
        _gripPx = LayoutEngine.Scale(GripLogicalPx, dpi);
        _headerPx = LayoutEngine.Scale(LayoutEngine.HeaderLogical, dpi);
        _dragThresholdPx = LayoutEngine.Scale(8, dpi);
        _repeatRadiusPx = LayoutEngine.Scale(16, dpi);
        _wheelStepPx = LayoutEngine.Scale(60, dpi); // px за один щелчок колеса
    }

    void Scroll(int wheelDelta) => SetScrollOffset(_scrollOffset + wheelDelta * _wheelStepPx / 120);

    /// <summary>Абсолютный скролл ленты (колесо и драг скроллбара).</summary>
    internal void SetScrollOffset(int offset)
    {
        PInvoke.GetClientRect(_hwnd, out RECT client);
        int maxScroll = Math.Max(0, _layout.TotalHeight - (client.bottom - client.top));
        offset = Math.Clamp(offset, 0, maxScroll);

        if (offset == _scrollOffset)
            return;
        CancelHoverZoom(); // лента уезжает из-под курсора
        _scrollOffset = offset;
        PInvoke.InvalidateRect(_hwnd, null, false);
    }

    /// <summary>Клик по этой точке панели попадает в крестик закрытия?</summary>
    internal bool IsOverCloseButton(int x, int y)
        => HitTest(x, y) is { } li && Inside(LayoutEngine.CloseRect(li.Label), x, y);

    /// <summary>Курсор над панелью (или её скроллбаром) — показать скроллбар.</summary>
    internal void PointerSeen()
    {
        if (_pointerNearby)
            return;
        _pointerNearby = true;
        _scrollbar?.UpdateVisibility(true);
    }

    /// <summary>Похоже, курсор ушёл; скрываем скроллбар, только если он вне окна панели.</summary>
    internal void PointerMaybeGone()
    {
        if (!_pointerNearby)
            return;
        PInvoke.GetCursorPos(out System.Drawing.Point pt);
        PInvoke.GetWindowRect(_hwnd, out RECT wnd);
        if (pt.X >= wnd.left && pt.X < wnd.right && pt.Y >= wnd.top && pt.Y < wnd.bottom)
            return;
        _pointerNearby = false;
        _scrollbar?.UpdateVisibility(false);
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

    void OnPress(int x, int y, bool ctrl)
    {
        // «Ручка» сверху или пустая зона — перетаскивание всей панели
        if (y < _headerPx || HitTest(x, y) is not { } li)
        {
            CancelPendingActivation();
            _press = PressState.PanelDrag;
            PInvoke.SetCapture(_hwnd);
            PInvoke.SetCursor(SizeAllCursor);
            return;
        }

        // Второй клик двойного детектируем уже по НАЖАТИЮ (до отпускания
        // проходит ещё ~100 мс — окно ожидания успевало истечь): сворачиваем
        // окно системно прямо здесь, а его отпускание глотаем.
        if (_pendingLabelItem == li.Window && !li.IsStrip)
        {
            CancelPendingActivation();
            _swallowNextUp = true;
            bool wasForeground = li.Window.Hwnd == _tracker.ForegroundWindow;
            PInvoke.ShowWindow(li.Window.Hwnd, SHOW_WINDOW_CMD.SW_SHOWMINNOACTIVE);
            if (wasForeground)
                _switch.ActivateMostRecentExcept(li.Window.Hwnd);
            return;
        }

        // Нажатие по другому элементу отменяет ожидающую активацию
        CancelPendingActivation(unless: li.Window);

        // Клик не должен внезапно включать hover-лупу
        ClearHoverCandidate();

        _press = PressState.Pressed;
        _pressItem = li.Window;
        _pressX = x;
        _pressY = y;
        PInvoke.SetCapture(_hwnd);
    }

    void OnMove(int x, int y, bool ctrl)
    {
        switch (_press)
        {
            case PressState.Pressed:
                if (Math.Abs(x - _pressX) > _dragThresholdPx || Math.Abs(y - _pressY) > _dragThresholdPx)
                {
                    CancelHoverZoom();
                    CancelPendingActivation();
                    _press = PressState.Dragging;
                    PInvoke.SetCursor(SizeNSCursor);
                    PInvoke.InvalidateRect(_hwnd, null, false); // подсветка таскаемого
                    DragTo(x, y);
                }
                break;

            case PressState.Dragging:
                PInvoke.SetCursor(SizeNSCursor);
                DragTo(x, y);
                break;

            case PressState.PanelDrag:
                PInvoke.SetCursor(SizeAllCursor);
                break;

            case PressState.None:
                // Один hit-test на движение — общий для лупы, pan и крестика
                var over = HitTest(x, y);
                if (ctrl)
                {
                    ClearHoverCandidate();
                    CtrlPan(over, x, y);
                }
                else
                {
                    HoverZoomMove(over, x, y);
                }
                UpdateCloseHover(over, x, y);
                break;
        }
    }

    /// <summary>
    /// Hover-лупа: задержка над превью включает временный zoom,
    /// движение панорамирует, уход с превью — восстановление.
    /// </summary>
    void HoverZoomMove(LayoutItem? over, int x, int y)
    {
        bool overPreview = over is { IsStrip: false } li && Inside(li.Preview, x, y);

        if (_hoverZoomItem is not null)
        {
            if (overPreview && over!.Value.Window == _hoverZoomItem)
            {
                PInvoke.SetCursor(SizeAllCursor);
                SetCenterFromPoint(_hoverZoomItem, x, y);
            }
            else
            {
                CancelHoverZoom();
            }
            return;
        }

        if (overPreview)
        {
            var item = over!.Value.Window;
            if (_hoverCandidate != item)
            {
                _hoverCandidate = item;
                PInvoke.SetTimer(_hwnd, HoverZoomTimerId, HoverZoomDelayMs, null);
            }
        }
        else
        {
            ClearHoverCandidate();
        }
    }

    void TryBeginHoverZoom()
    {
        var candidate = _hoverCandidate;
        _hoverCandidate = null;
        if (candidate is null || _press != PressState.None || _hoverZoomItem is not null)
            return;

        // Мышь всё ещё над этим же превью?
        var pt = GetCursorClientPos();
        if (HitTest(pt.X, pt.Y) is not { IsStrip: false } li || li.Window != candidate
            || !Inside(li.Preview, pt.X, pt.Y))
        {
            return;
        }

        _savedZoom = candidate.Zoom;
        _savedCenterX = candidate.CenterX;
        _savedCenterY = candidate.CenterY;
        _hoverZoomItem = candidate;

        // Лупа всегда ровно ×5, независимо от постоянного Ctrl-zoom
        candidate.Zoom = HoverZoomFactor;
        PInvoke.SetCursor(SizeAllCursor);
        SetCenterFromPoint(candidate, pt.X, pt.Y);
        PInvoke.InvalidateRect(_hwnd, null, false);
    }

    void CancelHoverZoom()
    {
        ClearHoverCandidate();
        if (_hoverZoomItem is null)
            return;

        _hoverZoomItem.Zoom = _savedZoom;
        _hoverZoomItem.CenterX = _savedCenterX;
        _hoverZoomItem.CenterY = _savedCenterY;
        _hoverZoomItem = null;
        PInvoke.InvalidateRect(_hwnd, null, false);
    }

    void ClearHoverCandidate()
    {
        if (_hoverCandidate is not null)
        {
            _hoverCandidate = null;
            PInvoke.KillTimer(_hwnd, HoverZoomTimerId);
        }
    }

    void UpdateCloseHover(LayoutItem? over, int x, int y)
    {
        WindowItem? hover = null;
        if (over is { } li && Inside(LayoutEngine.CloseRect(li.Label), x, y))
            hover = li.Window;

        if (hover != _hoverClose)
        {
            _hoverClose = hover;
            PInvoke.InvalidateRect(_hwnd, null, false);
        }

        // Всегда: иначе WM_MOUSELEAVE не придёт, и hover-лупа или подсветка
        // крестика «залипнут», когда мышь уйдёт с панели.
        var tme = new TRACKMOUSEEVENT
        {
            cbSize = (uint)sizeof(TRACKMOUSEEVENT),
            dwFlags = TRACKMOUSEEVENT_FLAGS.TME_LEAVE,
            hwndTrack = _hwnd,
        };
        PInvoke.TrackMouseEvent(ref tme);
    }

    void DragTo(int x, int y)
    {
        if (_pressItem is null)
            return;
        if (HitTest(x, y) is not { } over || over.Window == _pressItem)
            return;
        // Перетаскивание только внутри своей секции (живые / свёрнутые)
        if (over.Window.IsMinimized != _pressItem.IsMinimized)
            return;
        _tracker.Move(_pressItem, _tracker.IndexOf(over.Window));
    }

    void EndPress()
    {
        bool wasDragging = _press == PressState.Dragging;
        _press = PressState.None;
        _pressItem = null;
        PInvoke.ReleaseCapture();
        if (wasDragging)
            PInvoke.InvalidateRect(_hwnd, null, false); // снять подсветку таскаемого
    }

    /// <summary>Бросок панели: монитор под курсором, край — по половине монитора.</summary>
    unsafe void DropPanel()
    {
        PInvoke.GetCursorPos(out System.Drawing.Point pt);
        var hMonitor = PInvoke.MonitorFromPoint(pt, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);

        MONITORINFOEXW mi = default;
        mi.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
        if (!PInvoke.GetMonitorInfo(hMonitor, (MONITORINFO*)&mi))
            return;

        var mon = mi.monitorInfo.rcMonitor;
        var edge = pt.X < (mon.left + mon.right) / 2 ? DockEdge.Left : DockEdge.Right;
        string device = mi.szDevice.ToString();

        if (device == _settings.Monitor && edge == _settings.Edge)
            return;

        _settings.Monitor = device;
        _settings.Edge = edge;
        UpdatePosition();
        _settings.Save();
    }

    /// <summary>Центр видимой области по позиции курсора над превью данного окна.</summary>
    void SetCenterFromPoint(WindowItem item, int x, int y)
    {
        foreach (var li in _layoutItems)
        {
            if (li.Window != item || li.IsStrip)
                continue;

            var fit = LayoutEngine.FitRect(li.Preview, item.Aspect);
            int w = fit.right - fit.left, h = fit.bottom - fit.top;
            if (w <= 0 || h <= 0)
                return;

            item.CenterX = Math.Clamp((x - fit.left) / (double)w, 0, 1);
            item.CenterY = Math.Clamp((y - fit.top) / (double)h, 0, 1);
            PInvoke.InvalidateRect(_hwnd, null, false);
            return;
        }
    }

    void OnClick(int x, int y, bool ctrl)
    {
        if (HitTest(x, y) is not { } li)
            return;

        if (Inside(LayoutEngine.CloseRect(li.Label), x, y))
        {
            // Повторный клик в зоне крестика: после закрытия лента сдвинулась,
            // под курсором крестик чужого окна — не закрываем его случайно.
            if (IsRepeatClick(_closeClickTick, _closeClickX, _closeClickY, x, y))
                return;
            _closeClickTick = Environment.TickCount;
            _closeClickX = x;
            _closeClickY = y;
            PInvoke.PostMessage(li.Window.Hwnd, PInvoke.WM_CLOSE, default, default);
            return;
        }

        if (ctrl)
        {
            // Ctrl+клик — сброс zoom&pan этого превью
            li.Window.ResetZoom();
            PInvoke.InvalidateRect(_hwnd, null, false);
            return;
        }

        if (!li.IsStrip)
        {
            // Живой тайл: активация после короткого окна ожидания второго клика.
            // Сам второй клик (двойной = свернуть) перехватывается в OnPress.
            _pendingLabelItem = li.Window;
            PInvoke.SetTimer(_hwnd, ActivateTimerId, LabelActivateDelayMs, null);
            return;
        }

        // Полоска: мгновенный restore + переключение (Activate сам делает
        // SW_RESTORE). Второй клик двойного попадёт сюда же и ничего не
        // изменит — одинарный и двойной эквивалентны.
        _switch.Activate(li.Window.Hwnd);
    }

    void CancelPendingActivation(WindowItem? unless = null)
    {
        if (_pendingLabelItem is null || _pendingLabelItem == unless)
            return;
        PInvoke.KillTimer(_hwnd, ActivateTimerId);
        _pendingLabelItem = null;
    }

    /// <summary>Второй клик того же жеста: в пределах double-click-времени (с запасом) и рядом.</summary>
    bool IsRepeatClick(int tick, int px, int py, int x, int y)
    {
        int elapsed = Environment.TickCount - tick;
        if (elapsed < 0 || elapsed > (int)PInvoke.GetDoubleClickTime() + 150)
            return false;
        return Math.Abs(x - px) <= _repeatRadiusPx && Math.Abs(y - py) <= _repeatRadiusPx;
    }

    /// <summary>Ctrl+колесо над превью: постоянный zoom ×1..×5.</summary>
    void CtrlZoom(int wheelDelta)
    {
        CancelHoverZoom(); // ctrl управляет постоянным zoom, лупа не должна мешать
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
    void CtrlPan(LayoutItem? over, int x, int y)
    {
        if (over is not { IsStrip: false } li || li.Window.Zoom <= 1.001)
            return;
        PInvoke.SetCursor(SizeAllCursor);
        SetCenterFromPoint(li.Window, x, y);
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
        return _settings.Edge == DockEdge.Left
            ? clientX >= rc.right - _gripPx
            : clientX <= _gripPx;
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
            var autostart = MENU_ITEM_FLAGS.MF_STRING | (IsAutostartEnabled() ? MENU_ITEM_FLAGS.MF_CHECKED : 0);
            PInvoke.AppendMenu(menu, left, CmdDockLeft, Strings.DockLeft);
            PInvoke.AppendMenu(menu, right, CmdDockRight, Strings.DockRight);
            PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, null);
            PInvoke.AppendMenu(menu, autostart, CmdAutostart, Strings.Autostart);
            PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, null);
            PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, CmdExit, Strings.Exit);

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
                case CmdAutostart:
                    ToggleAutostart();
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

    static bool IsAutostartEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AutostartRunKey);
        return key?.GetValue(AutostartValue) is string;
    }

    static void ToggleAutostart()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(AutostartRunKey);
        if (key.GetValue(AutostartValue) is string)
            key.DeleteValue(AutostartValue, throwOnMissingValue: false);
        else
            key.SetValue(AutostartValue, $"\"{Environment.ProcessPath}\"");
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
