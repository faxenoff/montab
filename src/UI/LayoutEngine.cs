using Montab.Core;
using Windows.Win32.Foundation;

namespace Montab.UI;

internal readonly record struct LayoutItem(WindowItem Window, RECT Bounds, RECT Preview, RECT Label, bool IsStrip);

/// <summary>
/// Раскладка ленты: живые тайлы (превью по аспекту окна-источника + подпись)
/// и полоски (свёрнутые/погашенные). Все размеры — логические px × DPI.
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

    public int TotalHeight { get; private set; }

    public List<LayoutItem> Compute(IReadOnlyList<WindowItem> items, RECT client, uint dpi, int scrollOffset)
    {
        // C# 15: аргументы конструктора в collection expression
        List<LayoutItem> result = [with(capacity: items.Count)];

        int gap = Scale(GapLogical, dpi);
        int padding = Scale(PaddingLogical, dpi);
        int stripHeight = Scale(StripHeightLogical, dpi);
        int labelHeight = Scale(LabelHeightLogical, dpi);
        int minPreview = Scale(MinPreviewHeightLogical, dpi);

        int width = client.right - client.left - 2 * padding;
        if (width <= 0)
            return result;

        int left = client.left + padding;
        int y = Scale(HeaderLogical, dpi) + padding - scrollOffset;

        foreach (var item in items)
        {
            bool isStrip = item.IsStrip;
            RECT bounds, preview = default, label;

            if (isStrip)
            {
                bounds = new RECT { left = left, top = y, right = left + width, bottom = y + stripHeight };
                label = bounds;
            }
            else
            {
                double aspect = item.Aspect > 0.05 ? item.Aspect : 16.0 / 10.0;
                int previewHeight = Math.Clamp(
                    (int)Math.Round(width / aspect), minPreview, (int)(width * MaxPreviewHeightFactor));

                // Заголовок сверху, превью под ним
                bounds = new RECT { left = left, top = y, right = left + width, bottom = y + labelHeight + previewHeight };
                label = new RECT { left = left, top = y, right = left + width, bottom = y + labelHeight };
                preview = new RECT { left = left, top = y + labelHeight, right = left + width, bottom = bounds.bottom };
            }

            result.Add(new LayoutItem(item, bounds, preview, label, isStrip));
            y = bounds.bottom + gap;
        }

        TotalHeight = y + scrollOffset + padding - (items.Count > 0 ? gap : 0);
        return result;
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
