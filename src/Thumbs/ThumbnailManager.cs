using Montab.Core;
using Montab.UI;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace Montab.Thumbs;

/// <summary>
/// Владеет DWM-миниатюрами: регистрирует для видимых живых тайлов, снимает
/// с регистрации для полосок и ушедших за viewport (виртуализация — вне
/// экрана превью не стоит ничего). Один Sync после каждого пересчёта layout.
/// </summary>
internal sealed unsafe class ThumbnailManager(HWND panel) : IDisposable
{
    readonly HWND _panel = panel;
    readonly Dictionary<HWND, nint> _thumbs = [];
    readonly HashSet<HWND> _wanted = [];

    /// <summary>Затенение превью активного окна (255 = непрозрачно).</summary>
    const byte ActiveOpacity = 110;

    public void Sync(IReadOnlyList<LayoutItem> layout, RECT client, HWND activeWindow)
    {
        _wanted.Clear();

        foreach (var li in layout)
        {
            if (li.IsStrip)
                continue;
            if (li.Preview.bottom <= client.top || li.Preview.top >= client.bottom)
                continue; // вне viewport — поток не нужен

            HWND source = li.Window.Hwnd;
            _wanted.Add(source);

            if (!_thumbs.TryGetValue(source, out var thumb))
            {
                if (PInvoke.DwmRegisterThumbnail(_panel, source, out thumb).Failed)
                    continue;
                _thumbs[source] = thumb;
            }

            var dest = LayoutEngine.FitRect(li.Preview, li.Window.Aspect);
            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = PInvoke.DWM_TNP_RECTDESTINATION | PInvoke.DWM_TNP_VISIBLE |
                          PInvoke.DWM_TNP_OPACITY | PInvoke.DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = dest,
                opacity = source == activeWindow ? ActiveOpacity : (byte)255,
                fVisible = true,
                fSourceClientAreaOnly = true,
            };
            PInvoke.DwmUpdateThumbnailProperties(thumb, &props);
        }

        // Всё, что больше не нужно (полоска, за экраном, окно закрыто) — снять.
        List<HWND>? stale = null;
        foreach (var (hwnd, thumb) in _thumbs)
        {
            if (_wanted.Contains(hwnd))
                continue;
            PInvoke.DwmUnregisterThumbnail(thumb);
            (stale ??= []).Add(hwnd);
        }
        if (stale is not null)
        {
            foreach (var hwnd in stale)
                _thumbs.Remove(hwnd);
        }
    }

    public void Dispose()
    {
        foreach (var thumb in _thumbs.Values)
            PInvoke.DwmUnregisterThumbnail(thumb);
        _thumbs.Clear();
    }
}
