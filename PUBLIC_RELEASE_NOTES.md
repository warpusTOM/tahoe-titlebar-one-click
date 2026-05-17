# Public Release Notes

This public build keeps the installer redistributable:

- It does not bundle StartAllBack, StartAllBack license data, or private StartAllBack program files.
- It does not bundle Microsoft system DLLs.
- Private/test assets such as `ApplicationFrame.dll.patched`, `ApplicationFrame.patch.json`, `TahoeTraffic.theme`, and `TahoeTraffic.msstyles` are intentionally ignored by git unless redistribution rights are cleared.
- It cannot redistribute Microsoft DLLs or third-party private theme binaries, but it now generates `TahoeTraffic.theme` automatically and reuses an already-installed local `TahoeTraffic.msstyles` / compatible local mac-style theme when present.

The app includes the one-click UX:

- `Fix Everything Automatically`
- `Old Windows close/minimize/maximize + taskbar`

`Fix Everything Automatically` runs diagnosis first, applies every safe supported change, verifies the result, and opens a final report with `Full`, `Partial`, or `Failed` status. `Full` is reserved for machines where the core theme/msstyles are installed and active, and the Settings/UWP titlebar patch is actually applied; unsupported `ApplicationFrame.dll` hashes report `Partial` with a clear status reason.

Public builds still apply safe parts automatically:

- Brave, Chrome, and Edge native titlebar settings when profiles/shortcuts exist.
- Windows Terminal OS titlebar settings when the settings file exists.
- StartAllBack Tahoe taskbar/Start menu profile when StartAllBack is installed.
- DWM/dark/titlebar registry settings.

The Settings/UWP `ApplicationFrame.dll` patch is guarded by a supported-build/hash table plus an optional private `ApplicationFrame.patch.json` manifest. Unsupported builds are reported and skipped. The app never overwrites `ApplicationFrame.dll` unless the current file hash is explicitly supported or privately declared, a matching patch asset hash verifies, and a backup has been written.

### v0.3.4 Custom.theme active-theme verification

- Treats Windows `Custom.theme` as Tahoe-applied when its `[VisualStyles] Path` points to `TahoeTraffic\TahoeTraffic.msstyles`.
- Logs and reports the active visual style path, not only the active theme file path.
- Fixes false `Theme applied: no` reports after Windows copies the applied theme into the per-user Custom.theme file.

### v0.3.3 theme activation and private force patch

- Adds explicit theme activation/verification so the report separates `Theme installed` from `Theme applied`.
- Adds private force-patch support through `Assets\ApplicationFrame.patch.json` with exact original/patched SHA256 verification.
- Verifies the copied `ApplicationFrame.dll` hash after patching before reporting Settings/UWP success.
- Public release still does not redistribute Microsoft DLLs or patch unsupported hashes without a private manifest.

### v0.3.2 report honesty fix

- Does not report `Full` when the Settings/UWP `ApplicationFrame.dll` patch was skipped.
- Adds a `Status reason` and `Full min/max/close replacement` line to the final report.
- Keeps unsupported hashes safe: future builds can be added only with a verified original hash, verified patched hash, and matching private patch asset.

Backups are written to:

```text
C:\ProgramData\JhonLloydMolino\TahoeTitlebar\Backups
```

Private build with embedded local assets:

```powershell
dotnet publish .\TahoeTitlebarOneClick.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:EmbedPrivateAssets=true
```

### v0.3.1 asset fix

- Generates the missing Tahoe .theme file automatically.
- Uses embedded/sidecar assets first, then safely reuses local installed TahoeTraffic or Night Owl mac-style .msstyles assets when present.
- Keeps ApplicationFrame.dll patching guarded; no Microsoft DLL is uploaded in public release assets.
