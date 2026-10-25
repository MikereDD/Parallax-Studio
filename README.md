# Parallax Studio

<p align="center">
  <img src="src/ParallaxStudio.App/Assets/parallax-studio-icon-256.png" width="128" alt="Parallax Studio icon">
</p>

<h1 align="center">Parallax Studio</h1>

<p align="center">
  A premium Windows desktop video player in the <strong>Typezer∅ Studio Suite</strong>, built with C#, WPF, XAML, MVVM, and libmpv.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/version-1.0--dev.5.5.5-2d2d2d" alt="version 1.0-dev.5.5.5">
  <img src="https://img.shields.io/badge/.NET-10-2d2d2d?logo=dotnet&logoColor=white" alt=".NET 10">
  <img src="https://img.shields.io/badge/UI-WPF%20%2B%20XAML-2d2d2d" alt="WPF and XAML">
  <img src="https://img.shields.io/badge/pattern-MVVM-2d2d2d" alt="MVVM">
  <img src="https://img.shields.io/badge/backend-libmpv-2d2d2d" alt="libmpv">
</p>

> **Development status:** active pre-release development. Local-file, Blu-ray, and DVD playback are functional, but the project is not yet a stable v1.0 release.

Parallax Studio is a separate modern implementation of the original PowerShell/WinForms **Parallax** player. The classic application remains independent and is not replaced, modified, or used as a runtime dependency.

## The screening-room experience

Parallax Studio pairs a restrained cinematic shell with native video playback: dark layered surfaces, clear typography, quiet accents, and controls that give the picture room to breathe.

- **Cinema Mode:** Auto-Expand hides the sidebar during playback. Settings control its behavior when paused or stopped, and the sidebar can also be toggled manually.
- **Fullscreen:** the cinema surface fills the window, with floating controls in a separate owned window above the native video surface. Mouse movement, control timeout, cursor hiding, and Escape behavior are configurable; window bounds and the previous maximized state are restored on exit.
- **Now Playing v2:** a stronger title hierarchy, local artwork, playback progress, and compact source/video/audio badges. Media Details separates the available stream information from source, runtime, and chapter information, with loading feedback while optical metadata settles.
- **Local playback:** open or drop a video, then select audio, subtitles, and chapters; load external subtitles and adjust subtitle delay.
- **Player Experience v2:** a cinematic idle screen, local-file resume, selectable 5/10/15/30-second seek intervals, optional volume persistence, and optional double-click fullscreen. By default, resume starts after 30 seconds of playback and discards positions within the last minute of a title.
- **Appearance:** six themes—Dark Monochrome, OLED Black, Graphite, Midnight Indigo, Obsidian Gold, and Aurora Pulse—with Cadence, Precision, and Clear typography profiles. Appearance settings include reduced motion and comfortable/compact layout density.
- **Optical intelligence:** automatic drive/disc-change detection, cancellable title discovery, Blu-ray playback through libmpv/libbluray, and isolated DVD playback with runtime-based title reconciliation.

The picture remains on a native mpv HWND. The WPF shell and floating fullscreen controls are arranged around that surface; playback is not copied into a WPF bitmap or processed through shell effects.

### Local artwork

Now Playing looks for images beside a local media file, including a same-basename image (for example, `Arrival.mkv` with `Arrival.jpg`) and shared folder art named `poster.jpg`, `folder.jpg`, or `cover.jpg`. Keep artwork beside the media to use it without an online account or metadata service. Missing or unreadable artwork falls back to the built-in presentation.

This pass does not add online poster lookup or extract embedded attachments from media containers. Disc labels, title names, runtime, and stream details still depend on what the disc and playback backend expose; structural disc labels cannot supply authoritative episode names.

## Requirements

- Windows 10 or Windows 11, x64; the pinned libmpv package targets **x86-64-v3** CPUs
- .NET 10 SDK
- 7-Zip for native dependency bootstrap
- a DVD/Blu-ray optical drive for disc playback
- **MakeMKV installed locally** for the current protected Blu-ray/LibMMBD path and MakeMKV-assisted title discovery

Visual Studio with the **.NET desktop development** workload is optional; the `dotnet` CLI and included PowerShell build script are sufficient.

## Build and run

The normal development command is:

```powershell
.\build.ps1 -Bootstrap -InstallBootstrapTools -Run
```

The bootstrap reads `native-dependencies.json` and stages the native runtime files. The pinned libmpv archive and libdvdcss package are checked against manifest SHA-256 values before extraction. Installed output hashes are recorded in `native-dependencies.lock.json` and verified by `scripts/Verify-NativeDependencies.ps1`. Optional dependency failures are reported separately from the required libmpv runtime. Normal builds can reuse the staged files without downloading them again.

To build without bootstrapping:

```powershell
.\build.ps1 -Run
```

Or use the .NET CLI directly:

```powershell
dotnet restore .\ParallaxStudio.sln
dotnet build .\ParallaxStudio.sln -c Debug
dotnet run --project .\src\ParallaxStudio.App\ParallaxStudio.App.csproj
```

Stage native dependencies before using the direct CLI path; compilation alone does not download the playback engine. To check the staged native files explicitly:

```powershell
.\scripts\Verify-NativeDependencies.ps1
```

See [TESTING.md](TESTING.md) for the UI, local playback, protected Blu-ray, and DVD regression checks.

## Native playback dependencies

Parallax Studio deliberately keeps third-party native runtime handling explicit.

| Component | Role | Source/handling |
|---|---|---|
| `libmpv-2.dll` | Core playback engine | Pinned bootstrap dependency |
| `libaacs.dll` | Standard AACS fallback | Optional pinned bootstrap dependency |
| `libbdplus.dll` | Standard BD+ fallback | Optional pinned bootstrap dependency |
| `libdvdcss-2.dll` | CSS-protected DVD support | Pinned libdvdcss 1.5.0 package |
| MakeMKV `libmmbd64.dll` | Current protected Blu-ray compatibility provider | Detected from the user's local MakeMKV installation |
| MakeMKV CLI (`makemkvcon64.exe` / `makemkvcon.exe`) | Blu-ray/DVD title discovery metadata | Detected from the user's local MakeMKV installation |

### MakeMKV / LibMMBD integration

Parallax Studio's source repository **does not download or bundle MakeMKV**. When MakeMKV is installed locally, `build.ps1` looks for `libmmbd64.dll` under the standard MakeMKV installation directories. For the local development build it:

1. preserves bootstrapped standard providers as `libaacs.standard.dll` and `libbdplus.standard.dll` when present;
2. copies the locally installed `libmmbd64.dll` into the build output;
3. stages that same local file as runtime `libaacs.dll` and `libbdplus.dll`, which is the compatibility naming expected by the libbluray/libmpv disc path;
4. verifies the staged files against the local provider with SHA-256 before launch;
5. reports the active disc-decryption provider in the application log.

The playback service also accepts `PARALLAX_STUDIO_LIBMMBD` to point at a specific local `libmmbd64.dll` when the standard MakeMKV installation path is not appropriate.

**Redistribution note:** the build output may contain files copied from the user's local MakeMKV installation. Do not treat those locally staged files as Parallax-owned redistributable dependencies. Packaging/public-release work must review the applicable third-party licenses and redistribution terms before including any such binary.

See [docs/OPTICAL_MEDIA.md](docs/OPTICAL_MEDIA.md), [native/README.md](native/README.md), and [THIRD_PARTY.md](THIRD_PARTY.md) for the implementation and dependency boundaries.

## Optical media

### Blu-ray

Blu-ray playback uses libmpv/libbluray. Parallax can browse titles, identify likely main/bonus content, switch titles, and expose audio, subtitle, and chapter navigation. When MakeMKV LibMMBD is available, Parallax uses the locally installed provider for the protected-disc compatibility path. Standard `libaacs`/`libbdplus` remain available as a fallback path.

### DVD

DVD playback uses libmpv/libdvdnav with pinned `libdvdcss-2.dll` support for CSS-protected media. DVD playback is intentionally isolated in `ParallaxStudio.DvdPlaybackHost.exe` and controlled from the main application through a private named pipe. This process boundary prevents a native DVD backend failure from taking down the Parallax Studio UI.

MakeMKV and libdvdnav do not always number DVD titles the same way. Parallax probes the full libdvdnav edition table and maps discovered MakeMKV entries by runtime. Uncertain entries are rejected instead of silently playing the wrong program. Title discovery and reconciliation may take several seconds; the shell keeps source/title context visible while navigation and stream metadata become available.

Interactive DVD menus are not yet implemented.

### Disc detection and navigation

Ready Blu-ray and DVD sources appear automatically as drives change. Scans are serialized, and title discovery can be cancelled. The title browser presents disc-provided names, runtime, chapter counts, and likely main/bonus classifications where available. Opening an already populated browser reuses cached title metadata. An uncached title scan stops active optical playback first to avoid competing for the physical drive; selecting a title uses the existing Blu-ray or isolated DVD playback path.

## Keyboard controls

| Key | Action |
|---|---|
| Space | Play/pause |
| Left / Right | Seek by the configured interval |
| Up / Down | Change volume by 5% |
| M | Mute/unmute |
| F | Toggle fullscreen |
| Escape | Leave fullscreen, when enabled in Settings |

Selectors, text fields, toggles, and sliders retain their own keyboard navigation.

## Branding

The current Parallax Studio mark combines a dark cinematic `P`, an inset play symbol, layered depth rings, and a film-strip element. Multi-resolution Windows and PNG assets live under `src/ParallaxStudio.App/Assets/`; the executable uses `parallax-studio.ico` and the WPF shell uses the matching raster family.

## Runtime data

Parallax Studio keeps its state separate from classic Parallax:

```text
%LOCALAPPDATA%\ParallaxStudio\
├── settings.json
├── resume.json
├── cache\
└── logs\parallax-studio.log
```

The persistent log is the first place to inspect native playback, disc discovery, LibMMBD detection, title reconciliation, and runtime failures.

## Solution layout

```text
ParallaxStudio/
├── docs/                                Optical-media and implementation notes
├── native/                              Bootstrapped native runtime drop folder
├── scripts/                             Native bootstrap/verification tooling
├── src/
│   ├── ParallaxStudio.App/              WPF UI, ViewModels, themes, HWND video host
│   ├── ParallaxStudio.Core/             Playback/disc/track/resume models and contracts
│   ├── ParallaxStudio.Infrastructure/   libmpv, disc discovery, persistence, logging
│   ├── ParallaxStudio.DvdProbe/         Isolated DVD safety/runtime probe
│   └── ParallaxStudio.DvdPlaybackHost/  Isolated DVD playback process
├── ARCHITECTURE.md
├── CHANGELOG.md
├── ROADMAP.md
├── TESTING.md
├── THIRD_PARTY.md
├── native-dependencies.json
└── ParallaxStudio.sln
```

## Project boundary

Parallax Studio is an independent Typezer∅ Studio application. It does not share settings, resume state, or runtime files with the original PowerShell/WinForms Parallax. The original player remains the behavioral reference while functionality is ported and improved in focused vertical slices.

## Development direction

The current polish/intelligence pass improves Now Playing, local artwork, metadata presentation, and appearance controls while retaining the established native playback boundaries. Further work includes validating the DVD title map across more publishers/discs, persistent verified mappings, richer metadata where available, packaging, and release infrastructure.

See [ROADMAP.md](ROADMAP.md) for the staged plan and [CHANGELOG.md](CHANGELOG.md) for development history.
