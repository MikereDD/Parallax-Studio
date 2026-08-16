## Current optical-media milestone — v1.0-dev.5.5.5

- Validate full-disc DVD title mapping across additional television and movie DVDs.

- Reconcile MakeMKV DVD metadata with the program actually loaded by libdvdnav and keep the title browser refreshing until the verified match is visible.
- Learn per-disc runtime/chapter mappings without risking the Parallax UI process.
- Reuse verified mappings during later title selections in the same session.
- Next: persist mappings and resolve still-unknown titles automatically.


- DVD longest-title playback and direct numbered-title selection.
- DVD episode/bonus discovery with serialized drive access and cancellation.
- Audio, subtitle, and chapter parity with Blu-ray.
- Interactive DVD menus remain deferred until direct playback is proven reliable.

# Parallax Studio Roadmap

Parallax Studio uses its own version line. Development builds remain under the
`v1.0-dev.x` series until the modern application reaches validated feature parity
and stability suitable for `v1.0`.

## v1.0-dev.1 — Foundation

- [x] Independent solution and project identity
- [x] C# / WPF / XAML application shell
- [x] MVVM base classes and commands
- [x] Central theme and reusable control styles
- [x] Native WPF child-window video host
- [x] Core playback, track, disc, resume, settings, and logging contracts
- [x] Direct dynamic libmpv adapter
- [x] Foundation open, pause, stop, seek, and volume commands
- [x] Independent settings, resume store, and logging
- [x] Optical-drive discovery
- [x] Fullscreen and keyboard routing foundation
- [x] Original Parallax icon, build scripts, architecture notes, roadmap, and changelog

## v1.0-dev.2 — Playback state and controls

- [ ] Dedicated libmpv event loop with safe cancellation
- [ ] Observe pause, time-pos, duration, media-title, idle-active, and end-file
- [ ] Accurate position/duration slider with click-to-seek and drag scrubbing
- [ ] Play, pause, stop, ±5-second seek, volume, mute, and frame-step parity
- [ ] File drag-and-drop and recent-file handling
- [ ] Media-open and decoder errors surfaced without taking down the UI
- [ ] Stable keyboard shortcuts matching classic Parallax

## v1.0-dev.3 — Tracks and chapters

- [ ] Parse `track-list` into Core media-track models
- [ ] Audio and subtitle track picker with readable labels
- [ ] Subtitle Off state and forced/default indicators
- [ ] Parse chapters and expose timestamp navigation
- [ ] Refresh tracks after media changes and disc title changes
- [ ] Preserve deliberate user selection during the active session

## v1.0-dev.4 — Resume and session reliability

- [ ] Per-file watch-later checkpoints using the independent resume store
- [ ] Resume thresholds that avoid saving near the beginning or credits-end
- [ ] Restore prompt / automatic resume policy matching proven Parallax behavior
- [ ] Periodic crash-safe checkpoints
- [ ] Window size, position, state, volume, and last-folder restore
- [ ] Corrupt-state recovery UI and diagnostic export

## v1.0-dev.5 — DVD and Blu-ray workflows

- [ ] Disc picker with drive label, type, and readiness
- [ ] DVD and Blu-ray device configuration through libmpv
- [x] Title discovery and title switching
- [x] Chapter navigation for active disc titles
- [ ] Single-title play-all episode splitting and gap-detection workflow
- [x] Clear encrypted-disc dependency and failure reporting
- [x] Disc eject / media-change handling

## v1.0-dev.6 — Fullscreen and control auto-hide

- [ ] Borderless multi-monitor fullscreen on the active display
- [ ] Auto-hide controls and cursor after inactivity
- [ ] Immediate reveal on mouse or keyboard activity
- [ ] Reliable `F`, double-click, and `Esc` transitions
- [ ] Preserve and restore normal-window geometry
- [ ] Prevent focus loss and phantom taskbar exposure

## v1.0-dev.7 — Player polish

- [ ] Refined monochrome seek and volume sliders
- [ ] Rounded command buttons with hover, press, disabled, and focus states
- [ ] Compact and expanded player layouts
- [ ] Now-playing title treatment and track-panel polish
- [ ] High-DPI, ultrawide, multi-monitor, HDR, and VRR validation
- [ ] Accessibility names, keyboard focus, contrast, and screen-reader pass

## v1.0-dev.8 — Screenshots, packaging, and hardening

- [ ] Screenshot command and configurable save folder
- [ ] Portable ZIP publish
- [ ] Optional self-contained Windows x64 publish
- [ ] Automated Core and Infrastructure tests
- [ ] Long-playback, repeated-open, disc, resume, and fullscreen soak tests
- [ ] Upgrade-safe settings schema and reproducible version stamping
- [ ] Final README screenshots and user guide

## v1.0 — Stable

- [ ] Proven Parallax playback, disc, tracks, resume, fullscreen, and control workflows ported
- [ ] No dependency on the PowerShell/WinForms runtime
- [ ] Stable native resource ownership and shutdown
- [ ] Clean install, run, update, and recovery paths
- [ ] Original Parallax remains available as the stable classic implementation

## Parked until after v1.0

- Network streams and URL history UI
- Shader profile management
- Advanced video/audio filters
- Playlist and media-library features
- Picture-in-picture / mini-player
- Windows media controls and taskbar thumbnail commands
- Automatic metadata providers

These remain parked so they cannot destabilize the parity roadmap.
