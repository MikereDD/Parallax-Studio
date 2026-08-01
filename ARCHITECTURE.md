# Parallax Studio Architecture

## Goal

Parallax Studio is a separate modern Windows desktop implementation of Parallax.
It preserves the original player's proven behavior while replacing the
PowerShell/WinForms implementation with a maintainable C#, WPF, XAML, MVVM, and
libmpv architecture.

The original Parallax remains the stable behavioral reference. It is not a
runtime dependency and its files are never modified by Parallax Studio.

## Dependency rule

```text
ParallaxStudio.App ──────────────────────┐
        │                                │
        ├──> ParallaxStudio.Core         │
        │                                │
        └──> ParallaxStudio.Infrastructure ───> ParallaxStudio.Core
```

### ParallaxStudio.Core

Core contains player concepts only:

- playback state and source snapshots;
- media tracks, chapters, disc sources, and resume entries;
- settings and backend status models;
- contracts for playback, settings, resume, discs, and logging;
- application version identity.

Core does not reference WPF, Windows dialogs, native DLL loading, JSON files, or
libmpv implementation types.

### ParallaxStudio.Infrastructure

Infrastructure implements machine-facing Core contracts. In dev.1 it owns:

- `%LOCALAPPDATA%\ParallaxStudio` paths;
- atomic JSON settings and resume storage;
- append-style application logging;
- optical-drive discovery and disc-kind classification;
- dynamic loading of `libmpv-2.dll`;
- the initial libmpv client API adapter.

The libmpv adapter accepts a native child-window handle through the Core
interface. It does not know about WPF or `HwndHost`.

### ParallaxStudio.App

App owns presentation and composition:

- WPF windows, controls, and resource dictionaries;
- ViewModels and commands;
- the `HwndHost`-based video child window;
- Windows file dialogs;
- custom title-bar and fullscreen view behavior;
- startup, exception handling, and service composition.

Code-behind is limited to view responsibilities: native host lifecycle, window
dragging, caption buttons, keyboard routing, fullscreen geometry, and capturing
window state. Playback actions and application state stay in the ViewModel and
services.

## Native video surface

WPF does not expose an HWND-backed rendering element directly. The App project
therefore owns a small `HwndHost` that creates a native `STATIC` child window.
When the handle is ready, the ViewModel passes it to `IPlaybackService`.
Infrastructure sets libmpv's `wid` option before `mpv_initialize`, allowing mpv
to render into that child window while keeping native interop out of the view.
WPF chrome and flyouts are kept outside the native rectangle because HWND-hosted
content participates in the classic WPF airspace boundary.

The initial adapter loads exports dynamically through `NativeLibrary`. This gives
Parallax Studio explicit DLL search paths, clean missing-DLL reporting, and no
compile-time dependency on a third-party .NET wrapper.

## Porting strategy

Each proven Parallax workflow is ported as a vertical slice:

1. define or refine Core models and contracts;
2. implement the libmpv or persistence adapter;
3. expose behavior through ViewModel state and commands;
4. build the WPF interaction;
5. add recovery, logging, and focused tests;
6. compare behavior with the original Parallax;
7. move to the next feature only after the slice is dependable.

This avoids a single rewrite in which playback, resume, discs, tracks, fullscreen,
and controls all fail together.

## State and storage

Parallax Studio uses a separate runtime location:

```text
%LOCALAPPDATA%\ParallaxStudio\
├── settings.json
├── resume.json
├── cache\
└── logs\parallax-studio.log
```

The original Parallax watch-later files and configuration are not read or written.
A future migration tool may import selected state only through an explicit user
action.

JSON writes use a temporary file followed by replacement. Invalid JSON is retained
with a `.corrupt-<timestamp>` suffix before safe defaults are loaded.

## Playback evolution

The playback boundary is intentionally broader than the dev.1 UI. Planned slices
behind `IPlaybackService` include:

- property observation for position, duration, pause, cache, and end-file events;
- track-list and chapter-list parsing;
- disc device and title control;
- deterministic resume checkpoints;
- subtitle visibility and track selection;
- screenshot and frame-step commands;
- safe shutdown and native event-loop cancellation.

The UI never binds directly to libmpv structs, pointers, or enum values.

## Testing direction

Core and most Infrastructure code are WPF-free. Planned test groups include:

- settings and resume round trips;
- corrupt JSON recovery;
- media-path normalization and resume thresholds;
- disc classification;
- track identity and default/forced selection rules;
- chapter and title mapping;
- fullscreen state transitions at the ViewModel boundary;
- libmpv command-array marshaling and error translation.

Native playback integration tests remain Windows-only and require an explicit
libmpv test runtime.
