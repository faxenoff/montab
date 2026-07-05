using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Montab.App;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Montab.UI;

/// <summary>
/// Полупрозрачный оверлей-скроллбар для трекпадов (клик-драг вместо колеса).
/// Отдельное layered owned-popup-окно: DWM компонует превью поверх окна панели
/// (включая её child-окна), а owned-окно в z-order всегда выше владельца —
/// значит, и выше всех превью. Per-pixel alpha (UpdateLayeredWindow): трек —
/// едва заметное чёрное затемнение, бегунок — плотный тёмный; серой «вуали»
/// поверх превью нет. Показывается, только когда лента переполнена и курсор
/// над панелью. Зона крестиков «дырявая» (HTTRANSPARENT) — клик по ✕ проходит
/// сквозь скроллбар в панель.
/// </summary>
internal sealed unsafe class ScrollBarWindow
{
    const string ClassName = "montab.scrollbar";
    const int WidthLogical = 14;
    const int MinThumbLogical = 24;

    // Композиция UpdateLayeredWindow: dst = src + dst×(1−srcA). Пиксель с A=0 и
    // ненулевым цветом даёт чистое аддитивное наложение (linear dodge) — тёмный
    // фон высветляется в серый, светлый почти не меняется, цвета не «серятся».
    static readonly uint TrackPixel = Additive(24);
    static readonly uint ThumbBodyPixel = Additive(80);
    static readonly uint ThumbBodyDragPixel = Additive(115);
    // Кайма — обычная полупрозрачность: страхует видимость бегунка на белом,
    // где аддитивная добавка исчезает.
    static readonly uint ThumbBorderPixel = Premultiply(170, 20);

    /// <summary>Аддитивный серый пиксель: A=0, цвет прибавляется к фону.</summary>
    static uint Additive(byte value) => ((uint)value << 16) | ((uint)value << 8) | value;

    /// <summary>Premultiplied-пиксель серого цвета: value × alpha в каждом канале.</summary>
    static uint Premultiply(byte alpha, byte value)
    {
        uint c = (uint)(value * alpha / 255);
        return ((uint)alpha << 24) | (c << 16) | (c << 8) | c;
    }

    static ScrollBarWindow? s_instance;

    readonly PanelWindow _panel;
    readonly HWND _hwnd;

    int _totalHeight, _viewportHeight, _scrollOffset;
    int _minThumbPx = 24;
    int _width, _height;
    bool _visible;
    bool _dragging;
    int _dragAnchor; // расстояние от точки захвата до верха бегунка

    // Кешированная 32bpp-поверхность для UpdateLayeredWindow
    HDC _dibDc;
    HBITMAP _dib;
    HGDIOBJ _dibOld;
    uint* _bits;
    int _surfaceWidth, _surfaceHeight;

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
    }

    /// <summary>Полоса у внешнего края панели (экранные координаты), от «ручки» до низа.</summary>
    public void Layout(RECT panelScreen, uint dpi, bool dockRight, int topOffset)
    {
        _width = LayoutEngine.Scale(WidthLogical, dpi);
        _minThumbPx = LayoutEngine.Scale(MinThumbLogical, dpi);
        int x = dockRight ? panelScreen.right - _width : panelScreen.left;
        int y = panelScreen.top + topOffset;
        _height = panelScreen.bottom - y;
        PInvoke.SetWindowPos(_hwnd, default, x, y, _width, _height,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER);
        if (_visible)
            Redraw();
    }

    /// <summary>Синхронизация с лентой; вызывается панелью после пересчёта раскладки.</summary>
    public void Update(int totalHeight, int viewportHeight, int scrollOffset, bool pointerNearby)
    {
        bool changed = totalHeight != _totalHeight || viewportHeight != _viewportHeight || scrollOffset != _scrollOffset;
        _totalHeight = totalHeight;
        _viewportHeight = viewportHeight;
        _scrollOffset = scrollOffset;

        UpdateVisibility(pointerNearby);
        if (changed && _visible)
            Redraw();
    }

    public void UpdateVisibility(bool pointerNearby)
    {
        bool want = (pointerNearby || _dragging) && _totalHeight > _viewportHeight;
        if (want == _visible)
            return;
        _visible = want;
        if (want)
            Redraw();
        PInvoke.ShowWindow(_hwnd, want ? SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE : SHOW_WINDOW_CMD.SW_HIDE);
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

            case PInvoke.WM_LBUTTONDOWN:
            {
                int y = (short)((lParam.Value >> 16) & 0xFFFF);
                var (thumbTop, thumbHeight) = ThumbMetrics();
                // По бегунку — тащим от точки захвата; по треку — телепорт центром
                _dragAnchor = (y >= thumbTop && y < thumbTop + thumbHeight) ? y - thumbTop : thumbHeight / 2;
                _dragging = true;
                PInvoke.SetCapture(hwnd);
                DragTo(y, thumbHeight);
                Redraw();
                return new LRESULT(0);
            }

            case PInvoke.WM_MOUSEMOVE:
            {
                if (_dragging)
                {
                    int y = (short)((lParam.Value >> 16) & 0xFFFF);
                    var (_, thumbHeight) = ThumbMetrics();
                    DragTo(y, thumbHeight);
                }
                _panel.PointerSeen();
                // Иначе не узнаем, что мышь ушла со скроллбара за пределы панели
                var tme = new TRACKMOUSEEVENT
                {
                    cbSize = (uint)sizeof(TRACKMOUSEEVENT),
                    dwFlags = TRACKMOUSEEVENT_FLAGS.TME_LEAVE,
                    hwndTrack = hwnd,
                };
                PInvoke.TrackMouseEvent(ref tme);
                return new LRESULT(0);
            }

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
                    Redraw();
                    _panel.PointerMaybeGone(); // курсор мог уйти с панели во время драга
                }
                return new LRESULT(0);
        }

        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    void DragTo(int y, int thumbHeight)
    {
        int span = _height - thumbHeight;
        if (span <= 0)
            return;
        double frac = Math.Clamp((y - _dragAnchor) / (double)span, 0.0, 1.0);
        _panel.SetScrollOffset((int)Math.Round(frac * (_totalHeight - _viewportHeight)));
    }

    (int Top, int Height) ThumbMetrics()
    {
        if (_totalHeight <= _viewportHeight || _height <= 0)
            return (0, _height);

        int height = Math.Clamp((int)((long)_height * _viewportHeight / _totalHeight), Math.Min(_minThumbPx, _height), _height);
        int top = (int)((long)(_height - height) * _scrollOffset / (_totalHeight - _viewportHeight));
        return (top, height);
    }

    /// <summary>Перерисовка per-pixel-alpha поверхности и подача её в композитор.</summary>
    void Redraw()
    {
        if (_width <= 0 || _height <= 0)
            return;
        EnsureSurface();

        uint bodyPixel = _dragging ? ThumbBodyDragPixel : ThumbBodyPixel;
        var (thumbTop, thumbHeight) = ThumbMetrics();
        int border = Math.Max(1, _width / 12); // тёмная кайма бегунка
        int thumbBottom = Math.Min(thumbTop + thumbHeight, _height);

        uint* px = _bits;
        int total = _width * _height;
        for (int i = 0; i < total; i++)
            px[i] = TrackPixel;
        for (int y = thumbTop; y < thumbBottom; y++)
        {
            bool edgeRow = y < thumbTop + border || y >= thumbBottom - border;
            uint* row = _bits + (long)y * _width;
            for (int x = 0; x < _width; x++)
            {
                bool edgeCol = x < border || x >= _width - border;
                row[x] = edgeRow || edgeCol ? ThumbBorderPixel : bodyPixel;
            }
        }

        var size = new SIZE { cx = _width, cy = _height };
        var srcPoint = new System.Drawing.Point(0, 0);
        var blend = new BLENDFUNCTION
        {
            BlendOp = (byte)PInvoke.AC_SRC_OVER,
            SourceConstantAlpha = 255,
            AlphaFormat = (byte)PInvoke.AC_SRC_ALPHA,
        };
        PInvoke.UpdateLayeredWindow(_hwnd, default, null, &size, _dibDc, &srcPoint,
            default, &blend, UPDATE_LAYERED_WINDOW_FLAGS.ULW_ALPHA);
    }

    void EnsureSurface()
    {
        if (_dibDc != default && _surfaceWidth == _width && _surfaceHeight == _height)
            return;

        DestroySurface();

        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = _width,
                biHeight = -_height, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            },
        };

        void* bits;
        _dib = PInvoke.CreateDIBSection(default, &bmi, DIB_USAGE.DIB_RGB_COLORS, &bits, default, 0);
        _bits = (uint*)bits;
        _dibDc = PInvoke.CreateCompatibleDC(default);
        _dibOld = PInvoke.SelectObject(_dibDc, (HGDIOBJ)_dib.Value);
        _surfaceWidth = _width;
        _surfaceHeight = _height;
    }

    void DestroySurface()
    {
        if (_dibDc == default)
            return;
        PInvoke.SelectObject(_dibDc, _dibOld);
        PInvoke.DeleteObject((HGDIOBJ)_dib.Value);
        PInvoke.DeleteDC(_dibDc);
        _dibDc = default;
        _bits = null;
    }
}
