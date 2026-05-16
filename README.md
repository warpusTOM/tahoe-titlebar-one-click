

One-click Windows titlebar customizer for the close, minimize, and maximize buttons.

The UI has two choices:

- Tahoe style close/minimize/maximize
- Old Windows close/minimize/maximize

It applies:

- `TahoeTraffic.theme`
- `TahoeTraffic.msstyles`
- Browser native titlebar settings for Brave, Chrome, and Edge
- Windows Terminal titlebar settings
- DWM / dark mode / titlebar registry settings
- A guarded `ApplicationFrame.dll` patch for Settings/UWP windows on the supported Windows build

Backups are written to:

```text
C:\ProgramData\JhonLloydMolino\TahoeTitlebar\Backups
```

The app runs as administrator and includes a restore option.

## Build

```powershell
dotnet publish .\TahoeTitlebarOneClick.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

Private test build with local assets embedded:

```powershell
dotnet publish .\TahoeTitlebarOneClick.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:EmbedPrivateAssets=true
```

## Assets

This public source tree does not ship private/test binary assets. Put redistributable assets in `Assets\` before publishing a private build:

- `TahoeTraffic.theme`
- `TahoeTraffic.msstyles`
- `ApplicationFrame.dll.patched`

Do not publish Microsoft system DLLs publicly.

## Public Release Note

Do not publicly redistribute Microsoft system DLLs as standalone assets. A private build can include the local tested `ApplicationFrame.dll.patched` for your own machines. A public release should keep guarded patching logic and avoid shipping Microsoft-owned binaries.
