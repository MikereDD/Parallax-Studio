# Native runtime dependencies

The `native/` directory is the local source/development staging area for Parallax Studio's native playback dependencies. Native DLLs and bootstrap downloads are intentionally excluded from Git; the manifest and documentation remain tracked.

Run:

```powershell
.\build.ps1 -Bootstrap -InstallBootstrapTools -Run
```

The bootstrap reads `native-dependencies.json`, verifies downloaded artifacts, and stages the pinned x64 runtime set used by the build:

- `libmpv-2.dll` — required playback backend
- `libaacs.dll` — optional standard AACS provider
- `libbdplus.dll` — optional standard BD+ provider
- `libdvdcss-2.dll` — optional CSS DVD provider; currently pinned to libdvdcss 1.5.0

No AACS keys are included or downloaded by Parallax Studio.

## MakeMKV LibMMBD

MakeMKV is not downloaded into this directory. When a local MakeMKV installation contains `libmmbd64.dll`, `build.ps1` stages that installed file into the **application build output**, not into this source `native/` folder.

For the local protected Blu-ray path the build keeps standard providers as `libaacs.standard.dll` / `libbdplus.standard.dll` when available, then stages the local MakeMKV provider as `libmmbd64.dll` and as the runtime compatibility aliases `libaacs.dll` / `libbdplus.dll`. SHA-256 comparison verifies the aliases match the selected local provider.

The source tree therefore remains free of MakeMKV binaries. Public packaging must separately review whether any locally staged third-party file can be redistributed.

See `../docs/OPTICAL_MEDIA.md` and `../THIRD_PARTY.md` for the full boundary.
