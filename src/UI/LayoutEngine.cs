using Montab.Core;
using Windows.Win32.Foundation;

namespace Montab.UI;

internal readonly struct LayoutItem(WindowItem window, RECT bounds, RECT preview, RECT label, bool isStrip)
{
    public readonly WindowItem Window = window;
    public readonly RECT Bounds = bounds;
    public readonly RECT Preview = preview;
    public readonly RECT Label = label;
    public readonly bool IsStrip = isStrip;
}

/// <summary>
/// Раскладка ленты: живые тайлы (превью по аспекту окна-источника + подпись)
/// и полоски свёрнутых окон. Все размеры предвычисляются на текущий DPI,
/// список раскладки переиспользуется между кадрами (ноль аллокаций в кадре).
/// </summary>
internal sealed class LayoutEngine
{
    /// <summary>Высота «ручки» сверху панели — за неё панель перетаскивают на другой монитор/край.</summary>
    public const int HeaderLogical = 14;

    const int StripHeightLogical = 22;
    const int LabelHeightLogical = 18;
    const int GapLogical = 6;
    const int PaddingLogical = 8;
    const int MinPreviewHeightLogical = 48;
    const double MaxPreviewHeightFactor = 1.6; // не выше 1.6 ширины (очень «портретные» окна)

    readonly List<LayoutItem> _result = [];

    // Предвычисленные размеры на текущий DPI
    uint _dpi;
    int _header, _strip, _label, _gap, _padding, _minPreview;

    public int TotalHeight { get; private set; }

    public IReadOnlyList<LayoutItem> Compute(IReadOnlyList<WindowItem> items, RECT client, uint dpi, int scrollOffset)
    {
        SetDpi(dpi);
        _result.Clear();

        int width = client.right - client.left - 2 * _padding;
        if (width <= 0)
            return _result;

        int left = client.left + _padding;
        int y = _header + _padding - scrollOffset;

        foreach (var item in items)
        {
            bool isStrip = item.IsMinimized;
            RECT bounds, preview = default, label;

            if (isStrip)
            {
                bounds = new RECT { left = left, top = y, right = left + width, bottom = y + _strip };
                label = bounds;
            }
            else
            {
                double aspect = item.Aspect > 0.05 ? item.Aspect : 16.0 / 10.0;
                int previewHeight = Math.Clamp(
                    (int)Math.Round(width / aspect), _minPreview, (int)(width * MaxPreviewHeightFactor));

                // Заголовок сверху, превью под ним
                bounds = new RECT { left = left, top = y, right = left + width, bottom = y + _label + previewHeight };
                label = new RECT { left = left, top = y, right = left + width, bottom = y + _label };
                preview = new RECT { left = left, top = y + _label, right = left + width, bottom = bounds.bottom };
            }

            _result.Add(new LayoutItem(item, bounds, preview, label, isStrip));
            y = bounds.bottom + _gap;
        }

        TotalHeight = y + scrollOffset + _padding - (items.Count > 0 ? _gap : 0);
        return _result;
    }

    void SetDpi(uint dpi)
    {
        if (dpi == _dpi)
            return;
        _dpi = dpi;
        _header = Scale(HeaderLogical, dpi);
        _strip = Scale(StripHeightLogical, dpi);
        _label = Scale(LabelHeightLogical, dpi);
        _gap = Scale(GapLogical, dpi);
        _padding = Scale(PaddingLogical, dpi);
        _minPreview = Scale(MinPreviewHeightLogical, dpi);
    }

    /// <summary>
    /// Вписывает прямоугольник с данным аспектом внутрь ячейки по центру.
    /// DWM сам сохраняет аспект, но прижимает к левому верхнему углу —
    /// поэтому точный rect считаем сами.
    /// </summary>
    public static RECT FitRect(RECT cell, double aspect)
    {
        int cellW = cell.right - cell.left;
        int cellH = cell.bottom - cell.top;
        if (cellW <= 0 || cellH <= 0 || aspect <= 0)
            return cell;

        int w = cellW, h = (int)Math.Round(cellW / aspect);
        if (h > cellH)
        {
            h = cellH;
            w = (int)Math.Round(cellH * aspect);
        }

        int x = cell.left + (cellW - w) / 2;
        int y = cell.top + (cellH - h) / 2;
        return new RECT { left = x, top = y, right = x + w, bottom = y + h };
    }

    /// <summary>Квадратная зона крестика закрытия у правого края подписи.</summary>
    public static RECT CloseRect(RECT label)
    {
        int size = label.bottom - label.top;
        return new RECT { left = label.right - size, top = label.top, right = label.right, bottom = label.bottom };
    }

    public static int Scale(int logical, uint dpi) => (int)(logical * dpi / 96.0);
}
