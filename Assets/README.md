# Assets

This public repo intentionally does not commit private/test binary assets:

- `TahoeTraffic.theme`
- `TahoeTraffic.msstyles`
- `ApplicationFrame.dll.patched`
- `ApplicationFrame.patch.json` for private force-patch hash verification

For a private local build, place those files in this folder before publishing.

For a public release, use only assets you have permission to redistribute. Do not publish Microsoft system DLLs.

Public EXE users can also put allowed sidecar assets beside the EXE:

```text
Assets\TahoeTraffic.theme
Assets\TahoeTraffic.msstyles
Assets\ApplicationFrame.dll.patched
Assets\ApplicationFrame.patch.json
```

The app diagnoses these files before install. Missing theme or `.msstyles` assets produce a `Partial` install report instead of a fake success. `ApplicationFrame.dll.patched` is used only when the local Windows `ApplicationFrame.dll` hash is listed in the supported-build table, or when a private `ApplicationFrame.patch.json` beside the EXE declares the exact original SHA256 and patched SHA256. The patch asset hash must match before the system DLL is overwritten.

If TahoeTraffic.theme is missing, the app can generate it automatically. If TahoeTraffic.msstyles is missing from embedded/sidecar assets, the app checks for already-installed local TahoeTraffic and Night Owl mac-style .msstyles files and uses those local machine assets without redistributing them in the public repo or release ZIP.

Private force-patch manifest example:

```json
{
  "id": "private-26200-8037",
  "windowsBuild": "Windows 10 Pro 25H2, build 26200.8037",
  "originalSha256": "CE3523DCFDBE417937CE98B3FD21C78498D018D756099BB76553AFE05E0948E2",
  "patchedSha256": "SHA256_OF_YOUR_PATCHED_APPLICATIONFRAME_DLL",
  "patchedAssetName": "ApplicationFrame.dll.patched"
}
```

This is intentionally private. Do not publish Microsoft DLLs in public release assets.

Optional custom theme bypass sidecar:

```text
Assets\SecureUxTheme_x64.msi
Assets\SecureUxTheme_ARM64.msi
Assets\SecureUxTheme_x86.msi
```

If the matching MSI is not provided, the app downloads the latest official SecureUxTheme MSI during Fix Everything Automatically when Windows keeps falling back to Aero.msstyles.
