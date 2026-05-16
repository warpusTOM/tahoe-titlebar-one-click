<div align="center">
  <h1>Jhon-Lloyd Molino Tahoe Titlebar</h1>
  <p><strong>One-click Tahoe-style close, minimize, maximize, taskbar, and Start menu glass for Windows.</strong></p>

  <img src="Assets/showcase.png" alt="Tahoe-style titlebar showcase on Windows" width="834" />

  <br><br>

  <a href="https://github.com/warpusTOM/tahoe-titlebar-one-click/releases/latest">
    <img src="https://img.shields.io/github/v/release/warpusTOM/tahoe-titlebar-one-click?label=release&color=22c55e" alt="Latest release" />
  </a>
  <img src="https://img.shields.io/badge/Windows-10%20%2F%2011-0078D4?logo=windows&logoColor=white" alt="Windows 10 / 11" />
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white" alt=".NET 8" />
  <img src="https://img.shields.io/badge/Backup-Restore-f59e0b" alt="Backup and restore" />
</div>

---

Tahoe Titlebar is a small Windows customizer that applies the macOS Tahoe-style traffic-light buttons to the normal Windows close, minimize, and maximize controls. When StartAllBack is already installed, it also applies the matching translucent taskbar and Start menu profile.

It was built for one-click testing across machines: run it as administrator, choose the Tahoe option, wait for it to finish, then restart affected apps.

## Download

Get the latest public build from:

[github.com/warpusTOM/tahoe-titlebar-one-click/releases/latest](https://github.com/warpusTOM/tahoe-titlebar-one-click/releases/latest)

The public release is intentionally safe to redistribute. It does not include private Microsoft system DLLs.

## One-Click Options

- **Tahoe style close/minimize/maximize + taskbar** applies the Tahoe titlebar setup and the StartAllBack taskbar profile when StartAllBack is detected.
- **Old Windows close/minimize/maximize + taskbar** restores from the latest backup when available.

Backups are written to:

```text
C:\ProgramData\JhonLloydMolino\TahoeTitlebar\Backups
```

## What It Changes

- Tahoe theme and `.msstyles` package when those assets are present.
- Browser native titlebar settings for Brave, Chrome, and Edge.
- Windows Terminal titlebar settings.
- DWM, dark mode, and titlebar-related registry settings.
- StartAllBack taskbar and Start menu glass profile when StartAllBack is already installed.
- Explorer taskbar visibility/alignment values that support the Tahoe taskbar profile.
- Guarded `ApplicationFrame.dll` replacement support for Settings/UWP titlebars on supported builds.

## StartAllBack Profile

This project does **not** bundle StartAllBack, its license, or its private program files. Install StartAllBack separately first, then run the one-click EXE.

The Tahoe profile applies these portable settings:

- Taskbar glass/translucency with alpha `26` and blur `0`.
- Start menu translucency with alpha `36` and blur `0`.
- Tahoe-style taskbar coloring, centered/spaced taskbar icon behavior, and Explorer taskbar cleanup.
- A locally generated neutral Tahoe traffic-light orb at:

```text
%LOCALAPPDATA%\StartAllBack\Orbs\Tahoe Traffic Orb.bmp
```

The installer backs up the previous StartAllBack and Explorer registry settings before changing them.

## Build

Public-safe build:

```powershell
dotnet publish .\TahoeTitlebarOneClick.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

Private test build with local assets embedded:

```powershell
dotnet publish .\TahoeTitlebarOneClick.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:EmbedPrivateAssets=true
```

## Private Assets

This source tree does not publish private/test binary assets. For your own private machine build, place redistributable or locally tested assets in `Assets\` before publishing:

- `TahoeTraffic.theme`
- `TahoeTraffic.msstyles`
- `ApplicationFrame.dll.patched`

Do not publicly redistribute Microsoft system DLLs as standalone assets.
