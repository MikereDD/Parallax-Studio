# Optical Media Architecture

This document records the current Parallax Studio DVD/Blu-ray playback design and the boundaries around third-party native components.

## Blu-ray path

Parallax Studio uses libmpv/libbluray for Blu-ray playback. The application configures the optical drive as the Blu-ray device and uses libmpv disc sources for main-feature and direct-title playback.

Title discovery is performed separately through a locally installed MakeMKV command-line executable when available. Discovery data is used for duration, chapter count, playlist/title metadata, likely-main-feature ranking, and the title browser. Discovery is serialized with playback access so MakeMKV and libmpv do not compete for the optical drive.

### Protected Blu-ray provider

The preferred development path for protected Blu-rays is a locally installed MakeMKV `libmmbd64.dll`.

At build/run time Parallax checks, in order:

1. `PARALLAX_STUDIO_LIBMMBD`, when explicitly configured;
2. the standard 32-bit Program Files MakeMKV installation directory;
3. the standard Program Files MakeMKV installation directory.

When a local provider is found, `build.ps1` stages the user's `libmmbd64.dll` into the application output and creates runtime compatibility aliases named `libaacs.dll` and `libbdplus.dll`. If the standard bootstrapped providers exist, they are preserved alongside the runtime as `libaacs.standard.dll` and `libbdplus.standard.dll`.

The staging step verifies that the alias files match the local `libmmbd64.dll` before launch. The playback service also sets process-local `LIBAACS_PATH` and `LIBBDPLUS_PATH` to the selected provider and records the active provider in the log.

If MakeMKV is unavailable, Parallax falls back to the standard bootstrapped `libaacs`/`libbdplus` path. Protected-disc success then depends on that provider's normal configuration and the specific disc.

## DVD path

DVD playback uses libmpv/libdvdnav and the pinned x64 `libdvdcss-2.dll` package for CSS-protected discs.

Earlier in-process DVD tests exposed native failures capable of terminating the WPF application. The production direction therefore isolates DVD playback in a dedicated child process:

```text
ParallaxStudio.App
    │
    ├── private named-pipe control/state IPC
    │
    └── ParallaxStudio.DvdPlaybackHost.exe
            └── libmpv / libdvdnav / libdvdcss
```

The host renders into Parallax Studio's native video surface while the main WPF process remains responsible for the surrounding UI. A DVD-host failure can be reported/restarted without taking down the main application.

A separate DVD probe is retained for bounded native inspection and full title-map construction.

## DVD title reconciliation

DVD authoring is inconsistent. MakeMKV discovery indices and libdvdnav `dvd://N` selectors can refer to different authored programs, and discs can expose aggregate/menu/technical titles in addition to episodes or features.

Parallax Studio therefore does not permanently assume that metadata title N equals DVD program N. v1.0-dev.5.5.5 reads the libdvdnav edition table and compares actual program runtimes against MakeMKV discovery metadata. Confident matches form a per-disc title map. Unknown/ambiguous selections are rejected rather than silently loading a different program.

Future work will persist verified mappings between sessions and add optional metadata enrichment for discs that do not contain useful title names.

## Disc identity and cache direction

Current title metadata is cached by disc identity rather than drive letter alone so a swap in the same optical drive does not reuse stale results. Persistent mapping should continue this rule and incorporate stable disc structure/label information rather than assuming volume labels encode a literal disc number.

## Diagnostics

Runtime diagnostics are written to:

```text
%LOCALAPPDATA%\ParallaxStudio\logs\parallax-studio.log
```

Useful messages include the detected disc-decryption provider, native library paths, MakeMKV discovery state, DVD host lifecycle, edition/title tables, playback load events, and title reconciliation results.

## Distribution boundary

The Parallax Studio source repository does not fetch MakeMKV. MakeMKV is an external user-installed application. Local development builds may copy files from that installation into their local build output. Release packaging must not assume those staged files may be redistributed; third-party licensing and distribution requirements must be reviewed before any public binary package includes them.
