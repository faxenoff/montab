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

    HFONT _font;
    uint _fontDpi;

    public void Paint(HWND hwnd, IReadOnlyList<LayoutItem> layout, HWND activeWindow, uint dpi)
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

            foreach (var li in layout)
            {
                if (li.Bounds.bottom < client.top || li.Bounds.top > client.bottom)
                    continue;

                bool isActive = li.Window.Hwnd == activeWindow;
                if (!li.IsStrip)
                    DrawPreviewFrame(mem, li, isActive, dpi);
                DrawLabel(mem, li, isActive, dpi);
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

    /// <summary>Рамка вокруг области, куда DWM компонует превью (рисовать поверх превью нельзя).</summary>
    void DrawPreviewFrame(HDC hdc, LayoutItem li, bool isActive, uint dpi)
    {
        RECT fit = LayoutEngine.FitRect(li.Preview, li.Window.Aspect);
        int border = LayoutEngine.Scale(isActive ? 2 : 1, dpi);
        var frame = new RECT
        {
            left = fit.left - border,
            top = fit.top - border,
            right = fit.right + border,
            bottom = fit.bottom + border,
        };
        var brush = isActive ? _accentBrush : _frameBrush;
        for (int i = 0; i < border; i++)
        {
            PInvoke.FrameRect(hdc, in frame, brush);
            frame.left++; frame.top++; frame.right--; frame.bottom--;
        }
    }

    void DrawLabel(HDC hdc, LayoutItem li, bool isActive, uint dpi)
    {
        RECT r = li.Label;
        PInvoke.FillRect(hdc, in r, isActive ? _stripActiveBrush : _stripBrush);

        if (isActive)
        {
            var bar = new RECT { left = r.left, top = r.top, right = r.left + LayoutEngine.Scale(3, dpi), bottom = r.bottom };
            PInvoke.FillRect(hdc, in bar, _accentBrush);
        }

        int iconSize = LayoutEngine.Scale(16, dpi);
        int pad = LayoutEngine.Scale(6, dpi);
        int iconX = r.left + pad;
        int iconY = r.top + (r.bottom - r.top - iconSize) / 2;

        if (li.Window.Icon != default)
        {
            PInvoke.DrawIconEx(hdc, iconX, iconY, li.Window.Icon, iconSize, iconSize, 0, default, DI_FLAGS.DI_NORMAL);
        }

        PInvoke.SetTextColor(hdc, li.IsStrip ? TextDimColor : TextColor);
        var textRect = new RECT
        {
            left = iconX + iconSize + pad,
            top = r.top,
            right = r.right - pad,
            bottom = r.bottom,
        };
        string title = li.Window.Title;
        fixed (char* p = title)
        {
            PInvoke.DrawText(hdc, p, title.Length, &textRect,
                DRAW_TEXT_FORMAT.DT_SINGLELINE | DRAW_TEXT_FORMAT.DT_VCENTER |
                DRAW_TEXT_FORMAT.DT_END_ELLIPSIS | DRAW_TEXT_FORMAT.DT_NOPREFIX);
        }
    }

    void EnsureFont(uint dpi)
    {
        if (_font != default && _fontDpi == dpi)
            return;

        if (_font != default)
            PInvoke.DeleteObject((HGDIOBJ)_font.Value);

        _font = PInvoke.CreateFont(
            -LayoutEngine.Scale(12, dpi), 0, 0, 0,
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
        if (_font != default)
            PInvoke.DeleteObject((HGDIOBJ)_font.Value);
    }
}
