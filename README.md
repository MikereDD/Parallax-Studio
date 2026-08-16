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

## Current milestone — v1.0-dev.5.5.5

The current milestone proves the optical-media architecture end to end:

- local video playback through an in-process libmpv engine;
- protected Blu-ray playback using a locally installed MakeMKV LibMMBD provider when available;
- MakeMKV-assisted Blu-ray and DVD title discovery;
- DVD playback inside an isolated playback host so a native DVD failure cannot terminate the WPF application;
- audio-track, subtitle, chapter, seeking, volume, mute, and fullscreen controls;
- serialized/cancellable optical-disc discovery and automatic disc-change detection;
- full-disc DVD title mapping by runtime rather than assuming MakeMKV and libdvdnav title numbers are identical.

DVD title names remain dependent on what the disc actually exposes. When a disc contains only structural labels, Parallax can identify programs by runtime/chapter structure but cannot invent authoritative episode names. Metadata enrichment and persistent per-disc mappings are planned later.

## Requirements

- Windows 10 or Windows 11, x64
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

The bootstrap reads `native-dependencies.json`, downloads the pinned native packages, verifies them, and stages the required runtime files. Normal builds can then run without re-downloading dependencies.

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

MakeMKV and libdvdnav do not always number DVD titles the same way. v1.0-dev.5.5.5 therefore probes the full libdvdnav edition table and maps discovered MakeMKV entries by runtime. Uncertain entries are rejected instead of silently playing the wrong program.

Interactive DVD menus are not yet implemented.

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

The immediate optical-media work is to validate the full-disc DVD map against more publishers/discs, persist verified mappings, improve title naming when metadata is sparse, and continue hardening disc ejection/title switching. The broader roadmap then returns to resume/session reliability, fullscreen polish, packaging, release infrastructure, and the final cinematic Studio presentation.

See [ROADMAP.md](ROADMAP.md) for the staged plan and [CHANGELOG.md](CHANGELOG.md) for development history.
