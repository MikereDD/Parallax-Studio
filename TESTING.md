# Parallax Studio v1.0-dev.5.1.2 Test

## Fresh native dependency bootstrap

Leave `native\` empty except for `README.md`, then run:

```powershell
.\build.ps1 -Bootstrap -InstallBootstrapTools -Run
```

Confirm the bootstrap reports and installs:

- `libmpv-2.dll`
- `libaacs.dll` (optional; warning-only if unavailable)
- `libbdplus.dll` (optional; warning-only if unavailable)
- `libdvdcss-2.dll` (optional; warning-only if unavailable)

Then test Blu-ray detection, main-feature playback, Browse Titles, Tracks, subtitles, chapters, and clean shutdown.

`libaacs` and `libbdplus` do not include disc keys. Protected discs may still require a user-supplied AACS configuration.
