using Montab.Config;
using Montab.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Montab.App;

/// <summary>
/// Владелец приложения: один трекер окон на всю систему, по панели на каждый
/// включённый монитор и иконка в трее. Пересобирает набор панелей, когда
/// меняется конфигурация дисплеев или настройки.
/// </summary>
internal sealed unsafe class PanelHost
{
    readonly Settings _settings;
    readonly WindowTracker _tracker = new();
    readonly SwitchController _switch = new();
    readonly List<DisplayInfo> _displays = [];
    readonly Dictionary<string, PanelWindow> _panels = [];
    readonly List<string> _stale = [];
    readonly HINSTANCE _hInstance;
    TrayIcon? _tray;
    /// <summary>Быстрое «скрыть всё» из трея: панелей нет, но настройки мониторов целы.</summary>
    bool _hidden;

    public PanelHost(Settings settings)
    {
        _settings = settings;
        _hInstance = (HINSTANCE)PInvoke.GetModuleHandle(default(string)).Value;
    }

    public Settings Settings => _settings;

    public WindowTracker Tracker => _tracker;

    public SwitchController Switch => _switch;

    /// <summary>Мониторы системы слева направо — порядок пунктов в меню трея.</summary>
    public IReadOnlyList<DisplayInfo> Displays => _displays;

    /// <summary>Все панели временно скрыты (левый клик по иконке в трее).</summary>
    public bool Hidden => _hidden;

    public void Start()
    {
        _tray = new TrayIcon(this, _hInstance);

        _tracker.Changed += OnTrackerChanged;
        _tracker.ForegroundChanged += _switch.OnForegroundChanged;
        // Автопереходы по истории не идут в свёрнутые окна
        _switch.IsEligibleTarget = hwnd => _tracker.TryGet(hwnd, out var item) && !item.IsMinimized;

        RefreshDisplays();
        _tracker.Start();
        _switch.OnForegroundChanged(_tracker.ForegroundWindow);
    }

    void OnTrackerChanged()
    {
        foreach (var panel in _panels.Values)
            panel.Invalidate();
    }

    /// <summary>Пересобирает набор панелей под текущие мониторы и настройки.</summary>
    public void RefreshDisplays()
    {
        DisplayList.Enumerate(_displays);

        // Монитор отключили, панель на нём выключили или скрыли всё — окно больше не нужно
        _stale.Clear();
        foreach (var (device, _) in _panels)
        {
            if (_hidden || !TryFind(device, out _) || !_settings.For(device).Enabled)
                _stale.Add(device);
        }
        foreach (var device in _stale)
        {
            _panels[device].Destroy();
            _panels.Remove(device);
        }

        foreach (var display in _displays)
        {
            var mon = _settings.For(display.Device);
            if (!mon.Enabled || _hidden)
                continue;

            if (_panels.TryGetValue(display.Device, out var existing))
            {
                existing.SetDisplay(display);
            }
            else
            {
                var panel = new PanelWindow(this, mon);
                panel.Create(_hInstance, display);
                _panels[display.Device] = panel;
            }
        }

        // Окна могли переехать вместе с изменившейся геометрией дисплеев
        _tracker.RefreshMonitors();
        _tray?.SyncIcon();
    }

    /// <summary>
    /// Левый клик по иконке в трее: убрать/вернуть панели на всех мониторах.
    /// Настройки мониторов не трогаются — при возврате всё как было.
    /// </summary>
    public void ToggleHidden()
    {
        _hidden = !_hidden;
        RefreshDisplays();
    }

    public void SetEnabled(string device, bool enabled)
    {
        var mon = _settings.For(device);
        // Включение панели из меню — явная просьба её показать, снимает «скрыть всё»
        bool unhide = enabled && _hidden;
        if (mon.Enabled == enabled && !unhide)
            return;

        mon.Enabled = enabled;
        _hidden &= !unhide;
        _settings.Save();
        RefreshDisplays();
    }

    public void SetEdge(string device, DockEdge edge)
    {
        var mon = _settings.For(device);
        if (mon.Edge == edge)
            return;
        mon.Edge = edge;
        _settings.Save();
        if (_panels.TryGetValue(device, out var panel))
            panel.Relayout();
    }

    /// <summary>
    /// Панель бросили на другой монитор: если он свободен — переезжаем туда,
    /// у занятого своя панель уже есть, и трогать её незачем.
    /// </summary>
    public void MovePanel(PanelWindow panel, HMONITOR target, int cursorX)
    {
        if (!TryFind(target, out var display))
            return;

        var mon = _settings.For(display.Device);
        if (mon.Enabled)
            return;

        mon.Enabled = true;
        mon.Edge = cursorX < (display.Rect.left + display.Rect.right) / 2 ? DockEdge.Left : DockEdge.Right;
        _settings.For(panel.Device).Enabled = false;
        _settings.Save();
        RefreshDisplays();
    }

    public void Save() => _settings.Save();

    public void Exit()
    {
        _settings.Save();

        foreach (var panel in _panels.Values)
            panel.Destroy();
        _panels.Clear();

        _tray?.Dispose();
        _tray = null;
        _tracker.Dispose();
        PInvoke.PostQuitMessage(0);
    }

    bool TryFind(string device, out DisplayInfo display)
    {
        foreach (var candidate in _displays)
        {
            if (candidate.Device == device)
            {
                display = candidate;
                return true;
            }
        }
        display = default;
        return false;
    }

    bool TryFind(HMONITOR handle, out DisplayInfo display)
    {
        foreach (var candidate in _displays)
        {
            if (candidate.Handle == handle)
            {
                display = candidate;
                return true;
            }
        }
        display = default;
        return false;
    }
}
