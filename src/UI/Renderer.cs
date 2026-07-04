using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Montab.UI;

/// <summary>
/// GDI-отрисовка панели с двойной буферизацией: фон, полоски с иконкой
/// и заголовком, акцент активного окна. (Возможная замена на Direct2D — позже.)
/// </summary>
internal sealed unsafe class Renderer : IDisposable
{
    // COLORREF = 0x00BBGGRR
    static readonly COLORREF Background = new(0x001E1E1E);
    static readonly COLORREF StripFill = new(0x002D2D2D);
    static readonly COLORREF StripActiveFill = new(0x003A3A3A);
    static readonly COLORREF Accent = new(0x00D47800); // #0078D4
    static readonly COLORREF TextColor = new(0x00E0E0E0);
    static readonly COLORREF TextDimColor = new(0x00909090);

    readonly HBRUSH _bgBrush = PInvoke.CreateSolidBrush(Background);
    readonly HBRUSH _stripBrush = PInvoke.CreateSolidBrush(StripFill);
    readonly HBRUSH _stripActiveBrush = PInvoke.CreateSolidBrush(StripActiveFill);
    readonly HBRUSH _accentBrush = PInvoke.CreateSolidBrush(Accent);
    readonly HBRUSH _frameBrush = PInvoke.CreateSolidBrush(new COLORREF(0x00404040));
    readonly HBRUSH _closeHoverBrush = PInvoke.CreateSolidBrush(new COLORREF(0x002311E8)); // #E81123
    readonly HBRUSH _dragFillBrush = PInvoke.CreateSolidBrush(new COLORREF(0x004A4A4A));
    readonly HBRUSH _dragFrameBrush = PInvoke.CreateSolidBrush(new COLORREF(0x00909090));

    HFONT _font;
    uint _fontDpi;

    public void Paint(HWND hwnd, IReadOnlyList<LayoutItem> layout, HWND activeWindow, uint dpi,
        Montab.Core.WindowItem? hoverClose = null, Montab.Core.WindowItem? dragged = null)
    {
        EnsureFont(dpi);

        HDC hdc = PInvoke.BeginPaint(hwnd, out PAINTSTRUCT ps);
        try
        {
            PInvoke.GetClientRect(hwnd, out RECT client);
            int width = client.right - client.left;
            int height = client.bottom - client.top;
            if (width <= 0 || height <= 0)
                return;

            HDC mem = PInvoke.CreateCompatibleDC(hdc);
            HBITMAP bmp = PInvoke.CreateCompatibleBitmap(hdc, width, height);
            HGDIOBJ oldBmp = PInvoke.SelectObject(mem, (HGDIOBJ)bmp.Value);
            HGDIOBJ oldFont = PInvoke.SelectObject(mem, (HGDIOBJ)_font.Value);
            PInvoke.SetBkMode(mem, BACKGROUND_MODE.TRANSPARENT);

            PInvoke.FillRect(mem, in client, _bgBrush);
            DrawHeaderGrip(mem, client, dpi);

            foreach (var li in layout)
            {
                if (li.Bounds.bottom < client.top || li.Bounds.top > client.bottom)
                    continue;

                bool isActive = li.Window.Hwnd == activeWindow;
                bool isDragged = li.Window == dragged;
                if (!li.IsStrip)
                    DrawPreviewFrame(mem, li, isActive, dpi);
                if (isDragged)
                    DrawOutline(mem, li.Bounds, _dragFrameBrush, LayoutEngine.Scale(2, dpi));
                DrawLabel(mem, li, isActive, dpi, li.Window == hoverClose, isDragged);
            }

            PInvoke.BitBlt(hdc, 0, 0, width, height, mem, 0, 0, ROP_CODE.SRCCOPY);

            PInvoke.SelectObject(mem, oldFont);
            PInvoke.SelectObject(mem, oldBmp);
            PInvoke.DeleteObject((HGDIOBJ)bmp.Value);
            PInvoke.DeleteDC(mem);
        }
        finally
        {
            PInvoke.EndPaint(hwnd, in ps);
        }
    }

    /// <summary>Гриппер-«ручка» сверху: за неё панель перетаскивают на другой монитор/край.</summary>
    void DrawHeaderGrip(HDC hdc, RECT client, uint dpi)
    {
        int header = LayoutEngine.Scale(LayoutEngine.HeaderLogical, dpi);
        int dot = Math.Max(2, LayoutEngine.Scale(2, dpi));
        int gap = LayoutEngine.Scale(6, dpi);
        int centerX = (client.left + client.right) / 2;
        int y = (header - dot) / 2;

        for (int i = -2; i <= 2; i++)
        {
            int x = centerX + i * gap - dot / 2;
            var r = new RECT { left = x, top = y, right = x + dot, bottom = y + dot };
            PInvoke.FillRect(hdc, in r, _frameBrush);
        }
    }

    /// <summary>
    /// Цельная рамка вокруг всего блока «заголовок + превью». Рисуется наружу
    /// от Bounds: внутри рисовать нельзя — DWM компонует превью поверх нашего GDI.
    /// </summary>
    void DrawPreviewFrame(HDC hdc, LayoutItem li, bool isActive, uint dpi)
    {
        DrawOutline(hdc, li.Bounds,
            isActive ? _accentBrush : _frameBrush,
            LayoutEngine.Scale(isActive ? 2 : 1, dpi));
    }

    static void DrawOutline(HDC hdc, RECT bounds, HBRUSH brush, int border)
    {
        var frame = new RECT
        {
            left = bounds.left - border,
            top = bounds.top - border,
            right = bounds.right + border,
            bottom = bounds.bottom + border,
        };
        for (int i = 0; i < border; i++)
        {
            PInvoke.FrameRect(hdc, in frame, brush);
            frame.left++; frame.top++; frame.right--; frame.bottom--;
        }
    }

    void DrawLabel(HDC hdc, LayoutItem li, bool isActive, uint dpi, bool closeHover, bool isDragged)
    {
        RECT r = li.Label;
        PInvoke.FillRect(hdc, in r, isDragged ? _dragFillBrush : isActive ? _stripActiveBrush : _stripBrush);

        int iconSize = LayoutEngine.Scale(14, dpi);
        int pad = LayoutEngine.Scale(5, dpi);
        int iconX = r.left + pad;
        int iconY = r.top + (r.bottom - r.top - iconSize) / 2;

        if (li.Window.Icon != default)
        {
            PInvoke.DrawIconEx(hdc, iconX, iconY, li.Window.Icon, iconSize, iconSize, 0, default, DI_FLAGS.DI_NORMAL);
        }

        var close = LayoutEngine.CloseRect(r);

        PInvoke.SetTextColor(hdc, li.IsStrip ? TextDimColor : TextColor);
        var textRect = new RECT
        {
            left = iconX + iconSize + pad,
            top = r.top,
            right = close.left - pad,
            bottom = r.bottom,
        };
        string title = li.Window.Title;
        fixed (char* p = title)
        {
            PInvoke.DrawText(hdc, p, title.Length, &textRect,
                DRAW_TEXT_FORMAT.DT_SINGLELINE | DRAW_TEXT_FORMAT.DT_VCENTER |
                DRAW_TEXT_FORMAT.DT_END_ELLIPSIS | DRAW_TEXT_FORMAT.DT_NOPREFIX);
        }

        // Крестик закрытия приложения
        if (closeHover)
            PInvoke.FillRect(hdc, in close, _closeHoverBrush);
        PInvoke.SetTextColor(hdc, closeHover ? new COLORREF(0x00FFFFFF) : TextDimColor);
        fixed (char* x = "✕")
        {
            PInvoke.DrawText(hdc, x, 1, &close,
                DRAW_TEXT_FORMAT.DT_SINGLELINE | DRAW_TEXT_FORMAT.DT_VCENTER |
                DRAW_TEXT_FORMAT.DT_CENTER | DRAW_TEXT_FORMAT.DT_NOPREFIX);
        }
    }

    void EnsureFont(uint dpi)
    {
        if (_font != default && _fontDpi == dpi)
            return;

        if (_font != default)
            PInvoke.DeleteObject((HGDIOBJ)_font.Value);

        _font = PInvoke.CreateFont(
            -LayoutEngine.Scale(10, dpi), 0, 0, 0,
            400 /* FW_NORMAL */, 0, 0, 0,
            FONT_CHARSET.DEFAULT_CHARSET,
            FONT_OUTPUT_PRECISION.OUT_DEFAULT_PRECIS,
            FONT_CLIP_PRECISION.CLIP_DEFAULT_PRECIS,
            FONT_QUALITY.CLEARTYPE_QUALITY,
            0, "Segoe UI");
        _fontDpi = dpi;
    }

    public void Dispose()
    {
        PInvoke.DeleteObject((HGDIOBJ)_bgBrush.Value);
        PInvoke.DeleteObject((HGDIOBJ)_stripBrush.Value);
        PInvoke.DeleteObject((HGDIOBJ)_stripActiveBrush.Value);
        PInvoke.DeleteObject((HGDIOBJ)_accentBrush.Value);
        PInvoke.DeleteObject((HGDIOBJ)_frameBrush.Value);
        PInvoke.DeleteObject((HGDIOBJ)_closeHoverBrush.Value);
        PInvoke.DeleteObject((HGDIOBJ)_dragFillBrush.Value);
        PInvoke.DeleteObject((HGDIOBJ)_dragFrameBrush.Value);
        if (_font != default)
            PInvoke.DeleteObject((HGDIOBJ)_font.Value);
    }
}
