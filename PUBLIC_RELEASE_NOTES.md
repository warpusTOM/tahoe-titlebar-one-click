# Public Release Notes

This public build keeps the installer redistributable:

- It does not bundle StartAllBack, StartAllBack license data, or private StartAllBack program files.
- It does not bundle Microsoft system DLLs.
- Private/test assets such as `ApplicationFrame.dll.patched`, `TahoeTraffic.theme`, and `TahoeTraffic.msstyles` are intentionally ignored by git unless redistribution rights are cleared.
- It cannot fully replace close/minimize/maximize visuals unless the Tahoe theme assets are embedded in a private build or supplied by the user in `.\Assets`.

The app includes the one-click UX:

- `Fix Everything Automatically`
- `Old Windows close/minimize/maximize + taskbar`

`Fix Everything Automatically` runs diagnosis first, applies every safe supported change, verifies the result, and opens a final report with `Full`, `Partial`, or `Failed` status.

Public builds still apply safe parts automatically:

- Brave, Chrome, and Edge native titlebar settings when profiles/shortcuts exist.
- Windows Terminal OS titlebar settings when the settings file exists.
- StartAllBack Tahoe taskbar/Start menu profile when StartAllBack is installed.
- DWM/dark/titlebar registry settings.

The Settings/UWP `ApplicationFrame.dll` patch is guarded by a supported-build/hash table. Unsupported builds are reported and skipped. The app never overwrites `ApplicationFrame.dll` unless the current file hash is explicitly supported, a matching patch asset exists, and a backup has been written.

Backups are written to:

```text
C:\ProgramData\JhonLloydMolino\TahoeTitlebar\Backups
```

Private build with embedded local assets:

```powershell
dotnet publish .\TahoeTitlebarOneClick.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:EmbedPrivateAssets=true
```
