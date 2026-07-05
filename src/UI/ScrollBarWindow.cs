using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Montab.App;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Montab.UI;

/// <summary>
/// Полупрозрачный оверлей-скроллбар для трекпадов (клик-драг вместо колеса).
/// Отдельное layered owned-popup-окно: DWM компонует превью поверх окна панели
/// (включая её child-окна), а owned-окно в z-order всегда выше владельца —
/// значит, и выше всех превью. Показывается, только когда лента переполнена
/// и курсор над панелью. Зона крестиков «дырявая» (HTTRANSPARENT) —
/// клик по ✕ проходит сквозь скроллбар в панель.
/// </summary>
internal sealed unsafe class ScrollBarWindow
{
    const string ClassName = "montab.scrollbar";
    const int WidthLogical = 14;
    const int MinThumbLogical = 24;
    const byte Alpha = 150;

    static ScrollBarWindow? s_instance;
    static readonly HBRUSH TrackBrush = PInvoke.CreateSolidBrush(new COLORREF(0x00181818));
    static readonly HBRUSH ThumbBrush = PInvoke.CreateSolidBrush(new COLORREF(0x00909090));
    static readonly HBRUSH ThumbDragBrush = PInvoke.CreateSolidBrush(new COLORREF(0x00C0C0C0));

    readonly PanelWindow _panel;
    readonly HWND _hwnd;

    int _totalHeight, _viewportHeight, _scrollOffset;
    int _minThumbPx = 24;
    bool _visible;
    bool _dragging;
    int _dragAnchor; // расстояние от точки захвата до верха бегунка

    public ScrollBarWindow(PanelWindow panel, HWND parent, HINSTANCE hInstance)
    {
        _panel = panel;
        s_instance = this;

        fixed (char* className = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &StaticWndProc,
                hInstance = hInstance,
                lpszClassName = className,
            };
            PInvoke.RegisterClassEx(&wc);
        }

        // Owner (не parent): owned-окно всегда над владельцем в z-order
        _hwnd = PInvoke.CreateWindowEx(
            WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW,
            ClassName, null, WINDOW_STYLE.WS_POPUP,
            0, 0, 0, 0, parent, default, hInstance, null);

        PInvoke.SetLayeredWindowAttributes(_hwnd, default, Alpha, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
    }

    /// <summary>Полоса у внешнего края панели (экранные координаты), от «ручки» до низа.</summary>
    public void Layout(RECT panelScreen, uint dpi, bool dockRight, int topOffset)
    {
        int width = LayoutEngine.Scale(WidthLogical, dpi);
        _minThumbPx = LayoutEngine.Scale(MinThumbLogical, dpi);
        int x = dockRight ? panelScreen.right - width : panelScreen.left;
        int y = panelScreen.top + topOffset;
        PInvoke.SetWindowPos(_hwnd, default, x, y, width, panelScreen.bottom - y,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER);
    }

    /// <summary>Синхронизация с лентой; вызывается панелью после пересчёта раскладки.</summary>
    public void Update(int totalHeight, int viewportHeight, int scrollOffset, bool pointerNearby)
    {
        if (totalHeight != _totalHeight || viewportHeight != _viewportHeight || scrollOffset != _scrollOffset)
        {
            _totalHeight = totalHeight;
            _viewportHeight = viewportHeight;
            _scrollOffset = scrollOffset;
            if (_visible)
                PInvoke.InvalidateRect(_hwnd, null, true);
        }
        UpdateVisibility(pointerNearby);
    }

    public void UpdateVisibility(bool pointerNearby)
    {
        bool want = (pointerNearby || _dragging) && _totalHeight > _viewportHeight;
        if (want == _visible)
            return;
        _visible = want;
        PInvoke.ShowWindow(_hwnd, want ? SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE : SHOW_WINDOW_CMD.SW_HIDE);
        if (want)
            PInvoke.InvalidateRect(_hwnd, null, true);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static LRESULT StaticWndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            return s_instance?.HandleMessage(hwnd, msg, wParam, lParam)
                ?? PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ScrollBar WndProc exception: {ex}");
            return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    LRESULT HandleMessage(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            case PInvoke.WM_NCHITTEST:
            {
                // Клик по крестику закрытия проходит сквозь скроллбар в панель
                var pt = new System.Drawing.Point((short)(lParam.Value & 0xFFFF), (short)((lParam.Value >> 16) & 0xFFFF));
                PInvoke.ScreenToClient(_panel.Handle, ref pt);
                if (_panel.IsOverCloseButton(pt.X, pt.Y))
                    return new LRESULT(unchecked((nint)(int)PInvoke.HTTRANSPARENT));
                return new LRESULT((nint)PInvoke.HTCLIENT);
            }

            case PInvoke.WM_MOUSEACTIVATE:
                return new LRESULT((nint)PInvoke.MA_NOACTIVATE);

            case PInvoke.WM_PAINT:
                Paint(hwnd);
                return new LRESULT(0);

            case PInvoke.WM_LBUTTONDOWN:
            {
                int y = (short)((lParam.Value >> 16) & 0xFFFF);
                var (thumbTop, thumbHeight, trackHeight) = ThumbMetrics(hwnd);
                // По бегунку — тащим от точки захвата; по треку — телепорт центром
                _dragAnchor = (y >= thumbTop && y < thumbTop + thumbHeight) ? y - thumbTop : thumbHeight / 2;
                _dragging = true;
                PInvoke.SetCapture(hwnd);
                DragTo(y, thumbHeight, trackHeight);
                return new LRESULT(0);
            }

            case PInvoke.WM_MOUSEMOVE:
                if (_dragging)
                {
                    int y = (short)((lParam.Value >> 16) & 0xFFFF);
                    var (_, thumbHeight, trackHeight) = ThumbMetrics(hwnd);
                    DragTo(y, thumbHeight, trackHeight);
                }
                _panel.PointerSeen();
                // Иначе не узнаем, что мышь ушла со скроллбара за пределы панели
                var tme = new Windows.Win32.UI.Input.KeyboardAndMouse.TRACKMOUSEEVENT
                {
                    cbSize = (uint)sizeof(Windows.Win32.UI.Input.KeyboardAndMouse.TRACKMOUSEEVENT),
                    dwFlags = Windows.Win32.UI.Input.KeyboardAndMouse.TRACKMOUSEEVENT_FLAGS.TME_LEAVE,
                    hwndTrack = hwnd,
                };
                PInvoke.TrackMouseEvent(ref tme);
                return new LRESULT(0);

            case PInvoke.WM_MOUSELEAVE:
                _panel.PointerMaybeGone();
                return new LRESULT(0);

            case PInvoke.WM_LBUTTONUP:
            case PInvoke.WM_CAPTURECHANGED:
                if (_dragging)
                {
                    _dragging = false;
                    if (msg == PInvoke.WM_LBUTTONUP)
                        PInvoke.ReleaseCapture();
                    _panel.PointerMaybeGone(); // курсор мог уйти с панели во время драга
                }
                return new LRESULT(0);
        }

        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    void DragTo(int y, int thumbHeight, int trackHeight)
    {
        int span = trackHeight - thumbHeight;
        if (span <= 0)
            return;
        double frac = Math.Clamp((y - _dragAnchor) / (double)span, 0.0, 1.0);
        _panel.SetScrollOffset((int)Math.Round(frac * (_totalHeight - _viewportHeight)));
    }

    (int Top, int Height, int Track) ThumbMetrics(HWND hwnd)
    {
        PInvoke.GetClientRect(hwnd, out RECT rc);
        int track = rc.bottom - rc.top;
        if (_totalHeight <= _viewportHeight || track <= 0)
            return (0, track, track);

        int height = Math.Clamp((int)((long)track * _viewportHeight / _totalHeight), Math.Min(_minThumbPx, track), track);
        int top = (int)((long)(track - height) * _scrollOffset / (_totalHeight - _viewportHeight));
        return (top, height, track);
    }

    void Paint(HWND hwnd)
    {
        HDC hdc = PInvoke.BeginPaint(hwnd, out PAINTSTRUCT ps);
        try
        {
            PInvoke.GetClientRect(hwnd, out RECT rc);
            PInvoke.FillRect(hdc, in rc, TrackBrush);

            var (top, height, _) = ThumbMetrics(hwnd);
            var thumb = new RECT { left = rc.left, top = top, right = rc.right, bottom = top + height };
            PInvoke.FillRect(hdc, in thumb, _dragging ? ThumbDragBrush : ThumbBrush);
        }
        finally
        {
            PInvoke.EndPaint(hwnd, in ps);
        }
    }
}
