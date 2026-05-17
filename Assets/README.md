# Assets

This public repo intentionally does not commit private/test binary assets:

- `TahoeTraffic.theme`
- `TahoeTraffic.msstyles`
- `ApplicationFrame.dll.patched`

For a private local build, place those files in this folder before publishing.

For a public release, use only assets you have permission to redistribute. Do not publish Microsoft system DLLs.

Public EXE users can also put allowed sidecar assets beside the EXE:

```text
Assets\TahoeTraffic.theme
Assets\TahoeTraffic.msstyles
Assets\ApplicationFrame.dll.patched
```

The app diagnoses these files before install. Missing theme or `.msstyles` assets produce a `Partial` install report instead of a fake success. `ApplicationFrame.dll.patched` is used only when the local Windows `ApplicationFrame.dll` hash is listed in the supported-build table and the patch asset hash matches the expected patched hash.
