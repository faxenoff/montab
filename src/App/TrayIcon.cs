using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Montab.Config;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Montab.App;

/// <summary>
/// Иконка в области уведомлений: левый клик убирает и возвращает панели на всех
/// мониторах (иконка при этом гаснет), правый — открывает меню со списком
/// мониторов, у каждого переключатели «включено» и «слева/справа».
/// Её скрытое окно заодно ловит смену конфигурации дисплеев и конец сеанса —
/// панелей может не быть вовсе, а приложение работать должно.
/// </summary>
internal sealed unsafe class TrayIcon : IDisposable
{
    const string ClassName = "montab.tray";
    const uint TrayCallback = 0x8000 + 1; // WM_APP + 1
    const uint IconId = 1;
    /// <summary>Иконка приложения в ресурсах exe (классический IDI_APPLICATION).</summary>
    const int AppIconResource = 32512;
    /// <summary>Яркость и непрозрачность иконки в состоянии «панели скрыты».</summary>
    const uint DimPercent = 60;

    // Команды монитора: CmdMonitorBase + индекс * MonitorStride + действие
    const uint CmdAutostart = 1;
    const uint CmdExit = 2;
    const uint CmdMonitorBase = 100;
    const uint MonitorStride = 4;
    const uint MonitorToggle = 0;
    const uint MonitorLeft = 1;
    const uint MonitorRight = 2;

    static bool s_classRegistered;

    readonly PanelHost _host;
    /// <summary>Explorer перезапустился — иконку нужно добавить заново.</summary>
    readonly uint _taskbarCreated = PInvoke.RegisterWindowMessage("TaskbarCreated");
    HWND _hwnd;
    HICON _icon;         // цветная: панели на экранах
    HICON _iconHidden;   // приглушённая: панели скрыты
    bool _showingHidden;

    public TrayIcon(PanelHost host, HINSTANCE hInstance)
    {
        _host = host;

        if (!s_classRegistered)
        {
            fixed (char* className = ClassName)
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = &StaticWndProc,
                    hInstance = hInstance,
                    lpszClassName = className,
                };
                if (PInvoke.RegisterClassEx(&wc) == 0)
                    throw new InvalidOperationException("RegisterClassEx failed");
            }
            s_classRegistered = true;
        }

        // Окно не показывается, но обычное (не message-only): меню всплывающего
        // типа требует владельца, способного стать foreground.
        _hwnd = PInvoke.CreateWindowEx(
            WINDOW_EX_STYLE.WS_EX_TOOLWINDOW,
            ClassName, "montab", WINDOW_STYLE.WS_POPUP,
            0, 0, 0, 0, default, default, hInstance, WindowRef.Pin(this));

        if (_hwnd == default)
            throw new InvalidOperationException("CreateWindowExW failed");

        int cx = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSMICON);
        int cy = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSMICON);
        _icon = LoadTrayIcon(hInstance, cx, cy);
        _iconHidden = CreateDimmed(_icon, cx, cy);
        Add();
    }

    static HICON LoadTrayIcon(HINSTANCE hInstance, int cx, int cy)
    {
        var handle = PInvoke.LoadImage(
            hInstance, new PCWSTR((char*)AppIconResource), GDI_IMAGE_TYPE.IMAGE_ICON, cx, cy,
            IMAGE_FLAGS.LR_DEFAULTCOLOR);

        // Без ресурса (отладочная сборка без иконки) — хотя бы системная заглушка
        return handle == default
            ? PInvoke.LoadIcon(default, PInvoke.IDI_APPLICATION)
            : new HICON(handle.Value);
    }

    /// <summary>
    /// Обесцвеченная и приглушённая копия иконки — состояние «панели скрыты».
    /// Отдельного ресурса нет намеренно: рисунок один, вариант считается из него.
    /// </summary>
    static HICON CreateDimmed(HICON source, int cx, int cy)
    {
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = cx,
                biHeight = -cy, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            },
        };

        void* bits;
        var color = PInvoke.CreateDIBSection(default, &bmi, DIB_USAGE.DIB_RGB_COLORS, &bits, default, 0);
        if (color == default)
            return source;

        var dc = PInvoke.CreateCompatibleDC(default);
        var oldBitmap = PInvoke.SelectObject(dc, (HGDIOBJ)color.Value);
        PInvoke.DrawIconEx(dc, 0, 0, source, cx, cy, 0, default, DI_FLAGS.DI_NORMAL);
        PInvoke.SelectObject(dc, oldBitmap);
        PInvoke.DeleteDC(dc);

        // Пиксели premultiplied: серый берём как есть, гасим цвет и альфу вместе
        uint* px = (uint*)bits;
        for (int i = 0; i < cx * cy; i++)
        {
            uint p = px[i];
            uint a = p >> 24;
            if (a == 0)
                continue;
            uint b = p & 0xFF, g = (p >> 8) & 0xFF, r = (p >> 16) & 0xFF;
            uint gray = (r * 77 + g * 151 + b * 28) >> 8;
            gray = gray * DimPercent / 100;
            a = a * DimPercent / 100;
            px[i] = (a << 24) | (gray << 16) | (gray << 8) | gray;
        }

        // 32-битной иконке маска не нужна по существу, но CreateIconIndirect её требует
        var mask = PInvoke.CreateBitmap(cx, cy, 1, 1, null);
        var info = new ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
        var dimmed = PInvoke.CreateIconIndirect(in info);

        PInvoke.DeleteObject((HGDIOBJ)color.Value);
        PInvoke.DeleteObject((HGDIOBJ)mask.Value);
        return dimmed == default ? source : dimmed;
    }

    NOTIFYICONDATAW NewData()
    {
        _showingHidden = _host.Hidden;
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE |
                     NOTIFY_ICON_DATA_FLAGS.NIF_TIP,
            uCallbackMessage = TrayCallback,
            hIcon = _showingHidden ? _iconHidden : _icon,
        };

        var tip = Strings.Tooltip.AsSpan();
        var buffer = data.szTip.AsSpan();
        tip[..Math.Min(tip.Length, buffer.Length - 1)].CopyTo(buffer);
        return data;
    }

    void Add()
    {
        var data = NewData();
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, in data);
    }

    /// <summary>Приводит иконку в соответствие состоянию панелей (вызывает хост).</summary>
    public void SyncIcon()
    {
        if (_hwnd == default || _showingHidden == _host.Hidden)
            return;
        var data = NewData();
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, in data);
    }

    void Remove()
    {
        if (_hwnd == default)
            return;
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = IconId,
        };
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, in data);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static LRESULT StaticWndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            if (msg == PInvoke.WM_NCCREATE)
                WindowRef.Bind(hwnd, lParam);

            var self = WindowRef.Get(hwnd) as TrayIcon;
            var result = self?.HandleMessage(hwnd, msg, wParam, lParam)
                ?? PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);

            if (msg == PInvoke.WM_NCDESTROY)
                WindowRef.Release(hwnd);
            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Tray WndProc exception: {ex}");
            return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    LRESULT HandleMessage(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        if (msg == _taskbarCreated)
        {
            Add();
            return new LRESULT(0);
        }

        switch (msg)
        {
            case PInvoke.WM_NCCREATE:
                _hwnd = hwnd;
                break;

            case TrayCallback:
                uint mouse = (uint)(lParam.Value & 0xFFFF);
                if (mouse == PInvoke.WM_LBUTTONUP)
                    _host.ToggleHidden(); // быстрое «убрать/вернуть панели на всех экранах»
                else if (mouse == PInvoke.WM_RBUTTONUP)
                    ShowMenu();
                return new LRESULT(0);

            case PInvoke.WM_DISPLAYCHANGE:
                _host.RefreshDisplays();
                return new LRESULT(0);

            case PInvoke.WM_CLOSE:
                _host.Exit();
                return new LRESULT(0);

            case PInvoke.WM_ENDSESSION:
                // Выключение/перезагрузка Windows: WM_DESTROY может не прийти
                if (wParam.Value != 0)
                    _host.Save();
                break;

            case PInvoke.WM_DESTROY:
                Remove();
                _hwnd = default;
                return new LRESULT(0);
        }

        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    void ShowMenu()
    {
        var menu = PInvoke.CreatePopupMenu();
        if (menu == default)
            return;

        try
        {
            var displays = _host.Displays;
            for (int i = 0; i < displays.Count; i++)
            {
                var display = displays[i];
                var mon = _host.Settings.For(display.Device);
                uint baseCmd = CmdMonitorBase + (uint)i * MonitorStride;

                var sub = PInvoke.CreatePopupMenu();
                if (sub == default)
                    continue;

                PInvoke.AppendMenu(sub, Check(MENU_ITEM_FLAGS.MF_STRING, mon.Enabled),
                    baseCmd + MonitorToggle, Strings.Enabled);
                PInvoke.AppendMenu(sub, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, null);
                PInvoke.AppendMenu(sub, Check(MENU_ITEM_FLAGS.MF_STRING, mon.Edge == DockEdge.Left),
                    baseCmd + MonitorLeft, Strings.DockLeft);
                PInvoke.AppendMenu(sub, Check(MENU_ITEM_FLAGS.MF_STRING, mon.Edge == DockEdge.Right),
                    baseCmd + MonitorRight, Strings.DockRight);

                PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING | MENU_ITEM_FLAGS.MF_POPUP,
                    (nuint)sub.Value, Strings.Monitor(i + 1, display.Width, display.Height, display.Primary));
            }

            PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, null);
            PInvoke.AppendMenu(menu, Check(MENU_ITEM_FLAGS.MF_STRING, Autostart.IsEnabled()),
                CmdAutostart, Strings.Autostart);
            PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, CmdExit, Strings.Exit);

            PInvoke.GetCursorPos(out System.Drawing.Point pt);
            // Классическая пара из MSDN: без неё меню трея не закрывается кликом мимо
            PInvoke.SetForegroundWindow(_hwnd);
            var cmd = PInvoke.TrackPopupMenu(
                menu,
                TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON |
                TRACK_POPUP_MENU_FLAGS.TPM_RIGHTALIGN | TRACK_POPUP_MENU_FLAGS.TPM_BOTTOMALIGN,
                pt.X, pt.Y, _hwnd, null);
            PInvoke.PostMessage(_hwnd, 0 /* WM_NULL */, default, default);

            Invoke((uint)cmd.Value);
        }
        finally
        {
            PInvoke.DestroyMenu(menu); // вместе с подменю
        }
    }

    static MENU_ITEM_FLAGS Check(MENU_ITEM_FLAGS flags, bool @checked)
        => @checked ? flags | MENU_ITEM_FLAGS.MF_CHECKED : flags;

    void Invoke(uint cmd)
    {
        if (cmd >= CmdMonitorBase)
        {
            int index = (int)((cmd - CmdMonitorBase) / MonitorStride);
            if (index >= _host.Displays.Count)
                return;

            string device = _host.Displays[index].Device;
            switch ((cmd - CmdMonitorBase) % MonitorStride)
            {
                case MonitorToggle:
                    _host.SetEnabled(device, !_host.Settings.For(device).Enabled);
                    break;
                case MonitorLeft:
                    _host.SetEdge(device, DockEdge.Left);
                    break;
                case MonitorRight:
                    _host.SetEdge(device, DockEdge.Right);
                    break;
            }
            return;
        }

        switch (cmd)
        {
            case CmdAutostart:
                Autostart.Toggle();
                break;
            case CmdExit:
                _host.Exit();
                break;
        }
    }

    public void Dispose()
    {
        Remove();
        if (_hwnd != default)
            PInvoke.DestroyWindow(_hwnd);
        if (_iconHidden != default && _iconHidden != _icon)
            PInvoke.DestroyIcon(_iconHidden);
        if (_icon != default)
            PInvoke.DestroyIcon(_icon);
        _icon = _iconHidden = default;
    }
}
