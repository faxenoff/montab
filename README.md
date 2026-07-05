# montab

**EN** | [RU](README_RU.md)

A Windows sidebar taskbar with **always-on live previews** of every open
window. Docks to the left or right edge, reserves the work area (maximized
windows never overlap the panel), previews update in real time straight from
the DWM compositor — practically free in terms of resources.

A single **1.95 MB** exe (.NET 11 NativeAOT, zero dependencies), ~4 MB of
private memory, ~0% CPU when idle.

## Features

- Live previews of all windows from all monitors, aspect ratio preserved.
- Two-section list: live previews on top (new windows go to the very top),
  minimized windows below as compact strips with an icon and title.
  A window that gets minimized becomes the first strip; a restored one
  becomes the last live tile.
- The active window is highlighted with an accent frame, its preview dimmed.
- After minimizing, focus goes to the most recently used open window
  (minimized ones are skipped).
- Virtualization: previews scrolled out of view consume nothing.
- The panel remembers its monitor, edge and width between runs
  (`%APPDATA%\montab\settings.json`).

## Controls

| Action | Result |
|---|---|
| Click a live preview | Switch to the window (~150 ms delay — to distinguish from a double click) |
| Click a strip | Instant restore + switch (double click does the same) |
| Double-click a live preview (anywhere) | Minimize the window (real system minimize) into a strip |
| Click ✕ in the title bar | Close the application |
| Mouse wheel | Scroll the list |
| Drag the overlay scrollbar | Trackpad-friendly scrolling; the wide translucent bar appears at the outer edge on hover when the list overflows. ✕ buttons stay clickable through it |
| Hover over a preview (~0.7 s) | Temporary ×5 magnifier; mouse movement pans; leaving the preview restores |
| Ctrl + wheel over a preview | Persistent zoom ×1–5 |
| Ctrl + mouse move | Pan the zoomed preview |
| Ctrl + click | Reset zoom/pan |
| Drag a preview | Reorder (within its own section; the dragged item is highlighted) |
| Drag the top handle or empty area | Move the panel to another monitor/edge (edge picked by monitor half) |
| Drag the inner edge | Panel width (3–50% of monitor width) |
| Right click | Menu: dock edge, autostart, exit |

## Browser preview freezes?

Chromium browsers (Chrome, Brave, Edge) track native window occlusion: once
their window is fully covered by others, the browser stops rendering frames —
audio keeps playing while the panel preview freezes on the last frame. This is
not a montab limitation: DWM can only show what the application itself drew.

The fix is an official browser policy — run **one** PowerShell command for
your browser and restart it:

```powershell
# Brave
New-Item 'HKCU:\Software\Policies\BraveSoftware\Brave' -Force | Out-Null
New-ItemProperty 'HKCU:\Software\Policies\BraveSoftware\Brave' `
  -Name WindowOcclusionEnabled -Value 0 -PropertyType DWord -Force | Out-Null

# Chrome
New-Item 'HKCU:\Software\Policies\Google\Chrome' -Force | Out-Null
New-ItemProperty 'HKCU:\Software\Policies\Google\Chrome' `
  -Name WindowOcclusionEnabled -Value 0 -PropertyType DWord -Force | Out-Null

# Edge
New-Item 'HKCU:\Software\Policies\Microsoft\Edge' -Force | Out-Null
New-ItemProperty 'HKCU:\Software\Policies\Microsoft\Edge' `
  -Name WindowOcclusionEnabled -Value 0 -PropertyType DWord -Force | Out-Null
```

Registry-free alternative — launch flags in the browser shortcut:
`--disable-features=CalculateNativeWinOcclusion --disable-backgrounding-occluded-windows`.

The cost: a fully covered browser keeps spending GPU/CPU on rendering. Minimized windows are unaffected (they are strips in the panel anyway).

## Building

Requires the .NET 11 SDK (currently preview 5+) and MSVC (for NativeAOT
linking). Windows 10 1809+.

```powershell
dotnet build                           # dev build
dotnet publish -c Release -r win-x64   # single ~2 MB exe
```

The Release build is trimmed hard: no stack traces and no exception message
texts (`StackTraceSupport=false`, `UseSystemResourceKeys=true`) — use the dev
build for debugging.

If linking fails complaining about `vswhere.exe`, add to PATH:
`C:\Program Files (x86)\Microsoft Visual Studio\Installer`.

## Technology

Pure Win32 (no WPF/WinUI): DWM Thumbnail API (previews — the compositor
serves window frames with no capture or encoding), AppBar API (work-area
reservation), SetWinEventHook (window tracking without polling), GDI with a
cached backbuffer (rendering), CsWin32 (P/Invoke source generator, no
marshaling), Per-Monitor V2 DPI. .NET 11 + C# 15, NativeAOT. Zero allocations
in a steady frame. Details in [PLAN.md](PLAN.md).

## License

[MIT](LICENSE).
