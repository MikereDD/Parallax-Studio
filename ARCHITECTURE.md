# Parallax Studio Architecture

## Goal

Parallax Studio is a separate modern Windows desktop implementation of Parallax. It preserves the original player's proven behavior while replacing the PowerShell/WinForms implementation with a maintainable C#, WPF, XAML, MVVM, and libmpv architecture.

The original Parallax remains the stable behavioral reference. It is not a runtime dependency and its files are never modified by Parallax Studio.

## Dependency rule

```text
ParallaxStudio.App ──────────────────────┐
        │                                │
        ├──> ParallaxStudio.Core         │
        │                                │
        └──> ParallaxStudio.Infrastructure ───> ParallaxStudio.Core
```

The DVD probe/playback-host executables are process-isolation components. They reuse the playback contracts/protocol needed for DVD work but do not move UI responsibilities out of the App project.

### ParallaxStudio.Core

Core contains player concepts only: playback state/source snapshots, media tracks, chapters, disc sources, resume entries, settings/backend models, service contracts, and application identity.

Core does not reference WPF, Windows dialogs, native DLL loading, JSON files, or libmpv implementation types.

### ParallaxStudio.Infrastructure

Infrastructure owns machine-facing implementations:

- `%LOCALAPPDATA%\ParallaxStudio` paths;
- atomic JSON settings and resume storage;
- application logging;
- optical-drive discovery and disc classification;
- dynamic loading and control of `libmpv-2.dll`;
- MakeMKV-assisted title discovery;
- Blu-ray/DVD mapping, native-library diagnostics, and disc-provider selection;
- the client side of the isolated DVD playback host.

The libmpv adapter accepts a native child-window handle through the Core interface. It does not know about WPF or `HwndHost`.

### ParallaxStudio.App

App owns presentation and composition: WPF windows/controls/resources, ViewModels and commands, the native `HwndHost` video surface, Windows file dialogs, custom title bar/fullscreen behavior, startup/exception handling, and service composition.

Code-behind remains limited to view responsibilities such as native host lifecycle, window chrome, keyboard routing, fullscreen geometry, and window-state capture. Playback actions and application state stay in ViewModels/services.

## Native video surface

WPF does not expose an HWND-backed rendering element directly. The App project therefore owns a small `HwndHost` that creates a native child window. For local-file and Blu-ray playback, Infrastructure sets libmpv's `wid` before initialization so mpv renders into that child HWND.

WPF chrome and flyouts stay outside the native rectangle because HWND-hosted content participates in the classic WPF airspace boundary. Native exports are loaded dynamically through `NativeLibrary`, which gives Parallax Studio explicit DLL paths and clean missing-DLL reporting without a third-party .NET wrapper.

## Optical-media architecture

### Blu-ray

Blu-ray remains on the main in-process libmpv path. Parallax configures the disc device, opens libmpv/libbluray sources, and uses a locally installed MakeMKV command-line engine for richer title discovery when available.

For protected-disc development, a local MakeMKV `libmmbd64.dll` can be selected through `PARALLAX_STUDIO_LIBMMBD` or discovered in standard MakeMKV install locations. The build stages the selected local provider under the runtime compatibility names expected by the libbluray/libmpv path while preserving standard bootstrapped providers as fallbacks. The source repository itself does not contain or download MakeMKV.

### DVD

DVD playback is intentionally out-of-process. In-process libdvdnav failures proved capable of terminating the main WPF application even when the requested title was valid. The stable boundary is therefore:

```text
WPF App/ViewModel
      │
      ├── private named-pipe commands/state
      │
      └── DVD Playback Host
              └── libmpv / libdvdnav / libdvdcss
```

The isolated host renders into the existing Parallax video surface. If the native DVD stack terminates, the main application remains alive and can report/recover from the failure.

A separate probe process is used for bounded inspection and full-disc title-map construction.

### Title discovery and reconciliation

MakeMKV and libdvdnav can expose different title numbering for the same DVD. Parallax therefore treats discovery metadata and playback selectors as separate namespaces. The current mapper compares libdvdnav edition runtimes against MakeMKV-discovered runtimes and only creates confident mappings. Post-load runtime/chapter reconciliation verifies the selected program.

Disc-specific caches are keyed by disc identity rather than drive letter so replacing media in the same drive cannot reuse stale title metadata.

See `docs/OPTICAL_MEDIA.md` for operational details.

## State and storage

Parallax Studio uses a separate runtime location:

```text
%LOCALAPPDATA%\ParallaxStudio\
├── settings.json
├── resume.json
├── cache\
└── logs\parallax-studio.log
```

The original Parallax watch-later files and configuration are not read or written. JSON writes use a temporary file followed by replacement, and invalid JSON is retained with a `.corrupt-<timestamp>` suffix before safe defaults are loaded.

## Porting strategy

Each proven Parallax workflow is ported as a vertical slice:

1. define or refine Core models/contracts;
2. implement the native/persistence adapter;
3. expose state through ViewModels/commands;
4. build the WPF interaction;
5. add recovery, logging, and focused tests;
6. compare behavior with the original Parallax;
7. move forward only after the slice is dependable.

This keeps failures contained instead of combining playback, resume, discs, tracks, fullscreen, and UI changes into one rewrite.

## Testing direction

Core and most Infrastructure code remain WPF-free. Test groups include settings/resume round trips, corrupt JSON recovery, disc classification, track/chapter/title mapping, command marshaling, and state transitions. Native playback integration tests remain Windows-only and require the explicit native runtime and representative optical media.
