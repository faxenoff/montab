**EN** | [RU](PLAN_RU.md)

# montab — architecture and final requirements (as built)

> Status: fully implemented, see README for user-facing documentation.
> Actual metrics: single exe **1.95 MB** (NativeAOT, aggressive trimming),
> ~4 MB private memory, ~0% CPU when idle, zero allocations in a steady frame.
> Platform: `net11.0-windows` (preview) + C# 15.

A Windows sidebar taskbar with always-on live previews of every window.
Docks to the left or right of the work area; click to switch, hover to
magnify, double-click to minimize; minimal resource usage.

---

## 1. Key technology decisions

### 1.1. Previews: DWM Thumbnail API (not screen capture)

The core is `DwmRegisterThumbnail` / `DwmUpdateThumbnailProperties` /
`DwmUnregisterThumbnail`.

Why this and not Windows.Graphics.Capture / DXGI Duplication:

| Property | DWM Thumbnail | Windows.Graphics.Capture |
|---|---|---|
| Per-frame cost | ~0 (DWM composites the window's already-rendered surface, one textured quad) | Frame copy into a pool + our own drawing |
| "Bitrate" with no changes | Zero by construction: if the window didn't repaint, DWM doesn't recompose | Frames keep arriving, filtering is on us |
| Latency | Zero (same compositor) | 1+ frame |
| Pixel access | None (and none needed) | Yes |
| Occluded background windows | Works — DWM always renders windows to offscreen surfaces¹ | Works¹ |
| Minimized windows | Doesn't work | Doesn't work either |
| Source cropping (zoom&pan) | `rcSource` — built in | Manual |
| Opacity/dimming | `opacity` — built in | Manual |

¹ Except Chromium browsers with occlusion tracking enabled — they stop
rendering a fully covered window themselves. The fix (a registry policy) is
documented in the README.

Resource economy: there is no stream and no encoding at all, only GPU
composition. The main lever is **virtualization**: previews scrolled out of
the panel's viewport are unregistered and cost nothing.

Two hard-earned practical rules:
- **Set `rcSource` only while zoomed.** A pinned rcSource freezes the source
  size at call time; for a window animating out of the minimized state that is
  the 160×28 "iconic" strip → a pancake preview until the next event. Zoom
  reset is done by re-registering the thumbnail (DWM property flags only add
  fields, they cannot be unset).
- **Thumbnail content is composited OVER our GDI** within its rectangle —
  highlight frames and labels are drawn around the preview, never on top.

### 1.2. Platform: C# / .NET 11 + NativeAOT + pure Win32

- **Target**: `net11.0-windows`, `LangVersion=preview` (C# 15).
- **PublishAot + hard trimming**: `TrimMode=full`, `InvariantGlobalization`,
  `StackTraceSupport=false`, `UseSystemResourceKeys=true`,
  Debugger/EventSource/Metrics support off, `IlcFoldIdenticalMethodBodies`.
  GC: non-concurrent, `RetainVM=false`, `ConserveMemory=7`.
- **No UI framework**: neither WPF (no AOT) nor WinUI 3 (WindowsAppSDK is tens
  of MB). A raw Win32 window with our own WndProc.
- **P/Invoke**: `Microsoft.Windows.CsWin32` — a source generator,
  build-time-only dependency, `allowMarshaling=false`, `useSafeHandles=false`.
  The import list (`NativeMethods.txt`) is kept exact — only what is used.
- **Rendering**: GDI with a cached backbuffer (recreated only on resize) and
  per-DPI precomputed sizes. The originally planned Direct2D turned out to be
  unnecessary: GDI fully covers dark background + frames + text, and the
  previews themselves are drawn by DWM.
- **JSON settings**: System.Text.Json source generation (no reflection).

Static exe imports are OS-only: kernel32, advapi32 (autostart), bcrypt, ole32,
UCRT api-sets; user32/gdi32/dwmapi/shell32 load lazily on first call.

### 1.3. Panel docking: AppBar API

`SHAppBarMessage`: `ABM_NEW` → `ABM_QUERYPOS`/`ABM_SETPOS` with
`ABE_LEFT`/`ABE_RIGHT`. The system shrinks the work area itself — maximized
windows never overlap the panel. `ABN_FULLSCREENAPP` — drop to the bottom of
the z-order while a fullscreen app is active; `ABN_POSCHANGED` — recompute on
monitor configuration changes.

Important: the `WS_EX_TOPMOST` bit desyncs from the actual z-position —
`HWND_TOPMOST` is reasserted via `SetWindowPos` on every placement (just like
the system taskbar does).

### 1.4. Window tracking: events, not polling

- Initial inventory: `EnumWindows` + the classic alt-tab filter (Raymond
  Chen): `WS_VISIBLE`, non-empty title, no `WS_EX_TOOLWINDOW`, root owner of
  the owned chain, not cloaked (`DWMWA_CLOAKED` filters out windows of other
  virtual desktops).
- From then on — `SetWinEventHook`
  (`WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS`, callbacks arrive on our
  UI thread via the message loop):
  - `EVENT_OBJECT_SHOW / HIDE / DESTROY`, `CLOAKED / UNCLOAKED` — list membership;
  - `EVENT_SYSTEM_FOREGROUND` — active highlight + MRU history;
  - `EVENT_SYSTEM_MINIMIZESTART / MINIMIZEEND` — strip ⇄ live preview
    + move between sections;
  - `EVENT_OBJECT_NAMECHANGE` — label updates; also "late" registration of
    windows that set their title after being shown;
  - `EVENT_OBJECT_LOCATIONCHANGE` — source aspect recompute (guarded against
    "iconic" geometry: `IsIconic` + aspect clamp 0.2–4.5).

---

## 2. Architecture (actual)

One process, one UI thread with a message loop, one panel window per enabled
monitor plus a hidden tray window.

```
montab/
├─ src/
│  ├─ Program.cs              # entry point, single-instance mutex, message loop
│  ├─ App/
│  │  ├─ PanelHost.cs         # owner: tracker, switcher, per-monitor panels, tray
│  │  ├─ PanelWindow.cs       # HWND, WndProc, the whole interaction state machine,
│  │  │                       #   dock/resize/panel move, context menu, DPI cache
│  │  ├─ TrayIcon.cs          # Shell_NotifyIcon + monitor menu, WM_DISPLAYCHANGE
│  │  ├─ DisplayList.cs       # monitor enumeration (EnumDisplayMonitors)
│  │  ├─ WindowRef.cs         # HWND → window object via GWLP_USERDATA (many windows)
│  │  ├─ AppBar.cs            # SHAppBarMessage wrapper
│  │  ├─ Autostart.cs         # HKCU\...\Run
│  │  └─ Strings.cs           # RU/EN menu strings (GetUserDefaultUILanguage)
│  ├─ Core/
│  │  ├─ WindowTracker.cs     # EnumWindows + WinEventHook → two-section list
│  │  ├─ WindowItem.cs        # hwnd, title, icon, monitor, aspect, IsMinimized, zoom
│  │  └─ SwitchController.cs  # MRU foreground history + activation with fallbacks
│  ├─ Thumbs/
│  │  └─ ThumbnailManager.cs  # DWM thumbnails: virtualization, rcSource, opacity
│  ├─ UI/
│  │  ├─ LayoutEngine.cs      # layout (reusable buffer, per-DPI cache)
│  │  └─ Renderer.cs          # GDI: backbuffer, frames, labels, close button, grip
│  └─ Config/
│     └─ Settings.cs          # STJ source-gen; %APPDATA%\montab\settings.json
└─ montab.csproj              # net11.0-windows, PublishAot, trimming, CsWin32
```

### Multi-monitor: a panel on every screen

`PanelHost` keeps **one** `WindowTracker` and **one** `SwitchController` for the
whole app (WinEvent hooks and MRU history are global by nature) and creates as
many panels as there are enabled monitors. The settings key is the device name
from `MONITORINFOEXW.szDevice` (`\.\DISPLAY1`); an `HMONITOR` is not stable
across configuration changes and lives only inside a `DisplayInfo` snapshot.

- Each panel picks its own monitor's windows (`WindowItem.Monitor`) out of the
  shared list into a reusable buffer — filtering without allocations.
- A window's monitor is `MonitorFromWindow`; for a minimized one it is
  `MonitorFromRect` over `rcNormalPosition` from `GetWindowPlacement` — a
  minimized window physically sits at (-32000,-32000), where "nearest monitor"
  lies.
- `EVENT_OBJECT_LOCATIONCHANGE` recomputes the monitor, so a window dragged to
  the next screen moves to the other panel on its own.
- `WM_DISPLAYCHANGE` (caught by the tray window — there may be no panels at all)
  rebuilds the set of panels: vanished monitors lose theirs, new ones get a
  panel modelled on the first configured one.
- The `s_instance` singleton gave way to the classic GWLP_USERDATA: the object
  pointer is stored on `WM_NCCREATE` and released on `WM_NCDESTROY`.

### Tray

A hidden — but ordinary, not message-only, otherwise the menu won't dismiss on
an outside click — `montab.tray` window owns the `Shell_NotifyIcon` icon and
survives an explorer restart (`TaskbarCreated`).

- **Left click** — hide/restore the panels on every monitor. That is a separate
  host flag; per-monitor `Enabled` is untouched, so restoring brings everything
  back as it was. The flag is not persisted between runs, and enabling a monitor
  from the menu clears it (otherwise "Enabled" would do nothing).
- **Right click** — the menu: one item per monitor (`Display 1 · 2560×1600
  (primary)`) with an "Enabled" / "Dock left" / "Dock right" submenu, plus
  autostart and exit.

The icon is an application resource (`LoadImage(IDI_APPLICATION)`) built from
`assets/tray.svg` by `tools/make-icon.ps1`. The "panels hidden" variant is
derived from it at runtime: the icon is drawn into a 32-bit DIB, the pixels are
desaturated and dimmed to 60%, `CreateIconIndirect` — no second resource.

### Data model: a two-section list

`WindowItem`: hwnd, title, icon, source aspect, `IsMinimized`, zoom/center.
A single ordered list with an invariant: **live previews on top, minimized
strips below**. A window that minimizes is inserted at the section boundary
(first among strips); one that restores goes to the same boundary (last among
live) — the same insert operation. New live windows go to the very top, new
minimized ones become the first strip. The originally planned separate
"dimmed" (Collapsed) state was dropped: minimizing from the panel is a real
system minimize.

### Layout

- Panel width: clamped to **3–50%** of monitor width, resized by the inner edge.
- A "handle" at the top of the panel (14 logical px, grip dots) moves the panel.
- Live tile: label (18 logical px: icon + title + ✕) above the preview;
  preview height = width/aspect with clamping; the DWM rectangle is centered
  (DWM itself top-left-aligns).
- Strip: 22 logical px, icon + title + ✕.
- Gap 6 logical px; all sizes precomputed for the panel monitor's DPI.
- Wheel scrolling, clamped to total height.
- Overlay scrollbar for trackpads: a translucent layered owned-popup window
  (owned windows sit above their owner in z-order — i.e. above the DWM
  previews, which composite over the panel's own GDI and its child windows).
  Appears on hover when the list overflows; thumb drag / track teleport;
  the close-button zones return HTTRANSPARENT so ✕ clicks pass through.

### Window switching (final semantics)

- **Click on a live tile** (anywhere): activation delayed by 150 ms —
  the waiting window for a second click.
- **Double-click on a live tile**: system minimize (`SW_SHOWMINNOACTIVE`).
  The second click is detected **on the second button press** (DOWN), not the
  release: a real double click holds the button ~80–100 ms, so the UP→UP
  interval doesn't fit a reasonable delay while UP→DOWN does. The second
  click's release is swallowed. The system `WM_LBUTTONDBLCLK` is not used
  (the list reflows after the first click and the 4-pixel system zone misses).
- **Click on a strip**: instant restore + activation; a double click is
  equivalent to a single one (the second click is a no-op).
- **Click on the active window's tile**: the same waiting window, but instead of
  activating, the window is sent to the bottom of the z-order (`HWND_BOTTOM`)
  without being minimized, and focus goes to whatever ended up on top (a walk
  from `GetTopWindow`, limited to this monitor's windows). Top rather than most
  recent: with MRU, repeating the gesture would flip between two windows instead
  of walking the whole stack. That makes a pair: the first click switches to the
  app, the repeat click puts it back. A monitor's only live window is left
  alone: moving it in the z-order means nothing.
- **Right click on a live tile**: system minimize, immediately and for any tile
  (window state makes no difference here).
- The context menu now lives on the top handle and the empty part of the list —
  a right click on a tile is taken by the gestures above.
- After minimizing the active window, focus goes to the **most recently used
  open window per MRU history**; minimized and closed ones are skipped
  (32-entry history, the filter is supplied by the panel). The
  "repeat click on active → previous window" semantics was tried and removed
  as inconvenient.
- The panel is `WS_EX_NOACTIVATE` + topmost: clicks don't steal focus.
  Foreground-lock workaround: `keybd_event(VK_MENU)` when
  `SetForegroundWindow` refuses.

### Zoom & pan (final semantics)

- **Hover magnifier**: hovering over a preview for ~0.7 s → temporary zoom
  **×5** (exactly, not multiplied over the persistent zoom), mouse movement
  pans (SIZEALL cursor), leaving the preview / scrolling / starting a drag
  restores the previous state. A click cancels the pending magnifier intent —
  no flicker on plain switching.
- **Persistent zoom**: Ctrl+wheel ×1–5 (multiplicative steps), Ctrl+move —
  pan, Ctrl+click — reset. Implementation: `rcSource` = a 1/zoom fragment
  around the normalized center.
- The originally planned press-and-hold zoom was removed — it conflicted with
  drag-reorder.

### Drag-reorder and panel move

- Moving > 8 logical px from the press → tile dragging: live list reorder,
  ↕ cursor, the dragged item is highlighted (lighter fill + gray outline).
  Constrained to the item's own section. Order is not persisted between runs.
- Dragging the top handle or an empty area → panel move: dropping on either
  half of any monitor docks the panel to the corresponding edge (✥ cursor).

### Miscellaneous

- ✕ button in every label: red on hover, click posts `WM_CLOSE`; a repeat
  click at the same spot is ignored (the list has shifted — a different
  window's ✕ is under the cursor).
- Active window: accent frame around the whole block + preview dimming
  (`opacity` ≈ 110).
- Panel context menu: dock edge, hide the panel on this display, autostart
  (HKCU\...\Run), exit; localized (Russian system UI language → Russian,
  otherwise English).
- Notification windows get no tab: filtered out by `WS_EX_NOACTIVATE`, and also
  windows with no frame styles (`WS_THICKFRAME|WS_MINIMIZEBOX|WS_MAXIMIZEBOX|
  WS_SYSMENU`) and no `WS_CAPTION` **if** they are also smaller than half the
  monitor — otherwise borderless fullscreen windows (games, players) would be
  swept up too.
- Settings (edge, width, whether the panel is shown) are stored per monitor,
  saved on every change and on `WM_ENDSESSION`; a settings file from the
  previous version is read as the defaults for every monitor.
- Per-Monitor V2 DPI: physical pixels everywhere, DPI-dependent sizes
  precomputed on `WM_DPICHANGED`.

---

## 3. Deviations from the original plan

| Planned | Shipped | Why |
|---|---|---|
| Direct2D/DirectWrite | GDI + cached backbuffer | DWM draws the previews; D2D is overkill for background/frames/text |
| Repeat click on active → previous window | Removed | Inconvenient UX (confirmed in practice) |
| ×3 zoom on press-and-hold | Hover magnifier ×5 + Ctrl modes | Holding conflicted with drag-reorder |
| A separate "dimmed" (Collapsed) state besides minimize | Unified: strip = system-minimized window | Two mechanisms confused each other; a real minimize is more honest |
| Label below the preview | Label above the preview | Requested after real use |
| Deferred activation ~500 ms (system dblclick) | 150 ms + second-click detection on DOWN | The 4 px system zone missed due to list reflow |
| Persisting order between runs | Not persisted | Order "lives" with the windows; the value never materialized |
| Tray icon | Present (since 1.1) | There are several panels now and all of them can be off — an entry point outside the panel is needed |
| One panel on a chosen monitor | A panel per monitor, each with its own windows | Requested after real use |
| WGC-based strip "activity" detector | None | Expensive; events (restore/foreground) suffice |

## 4. Risks: what actually happened

| Risk from the plan | Outcome |
|---|---|
| `SetForegroundWindow` without panel activation | Partially confirmed; the Alt trick suffices |
| WS_EX_TOPMOST gets lost | Confirmed; fixed by SetWindowPos(HWND_TOPMOST) on every placement |
| DWM composites over our graphics | Confirmed; frames are drawn around previews |
| Chromium stops rendering occluded windows | New, not foreseen; documented in README (occlusion policy) |
| "Iconic" geometry during restore animation | New; fixed by unpinned rcSource + aspect filter |
| Virtual desktops | As planned: only the current desktop is shown (cloaked filtered out) |

## 5. Acceptance criteria — actual

- Single exe (NativeAOT) **1.95 MB**, zero external dependencies. ✓
- ~4 MB private memory; CPU ≈ 0% idle; GPU — DWM composition only. ✓
- Previews update at the source's own rate with no visible latency. ✓
- Aspect ratio, gaps, click-to-switch, double-click-to-minimize, drag-reorder,
  two-section list, scrolling + virtualization, ×5 hover magnifier and ×1–5
  Ctrl zoom, active highlight, 3–50% dock, cross-monitor moves, PMv2 DPI,
  autostart, settings persistence. ✓
