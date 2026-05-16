# Public Release Notes

This public build keeps the installer redistributable:

- It does not bundle StartAllBack, StartAllBack license data, or private StartAllBack program files.
- It does not bundle Microsoft system DLLs.
- Private/test assets such as `ApplicationFrame.dll.patched`, `TahoeTraffic.theme`, and `TahoeTraffic.msstyles` are intentionally ignored by git unless redistribution rights are cleared.

The app includes the one-click UX:

- `Tahoe style close/minimize/maximize + taskbar`
- `Old Windows close/minimize/maximize + taskbar`

When StartAllBack is already installed on the target PC, the public build applies the Tahoe translucent taskbar/Start menu profile and generates a local Tahoe traffic-light orb.

Backups are written to:

```text
C:\ProgramData\JhonLloydMolino\TahoeTitlebar\Backups
```
