# Parallax Studio Roadmap

Parallax Studio uses its own version line. Development builds remain under the `v1.0-dev.x` series until the modern application reaches validated feature parity and stability suitable for `v1.0`.

## Current optical-media milestone — v1.0-dev.5.5.5

- [x] Protected Blu-ray playback through locally installed MakeMKV LibMMBD when available
- [x] MakeMKV-backed Blu-ray title discovery and direct title switching
- [x] DVD playback isolated from the WPF process
- [x] DVD title discovery with serialized drive access and cancellation
- [x] Full-disc DVD title mapping by runtime instead of raw numeric assumptions
- [x] Audio, subtitle, chapter, seek, volume, and fullscreen controls during disc playback
- [x] Automatic optical-media change detection and disc-specific title caches
- [ ] Validate DVD mappings across additional publishers, TV sets, and movie discs
- [ ] Persist verified DVD mappings between sessions
- [ ] Improve unknown/structural DVD title naming with optional metadata enrichment
- [ ] Harden disc ejection and host recovery during active playback
- [ ] Interactive DVD menus (deferred until direct-title playback is fully hardened)

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
- [x] App icon, build scripts, architecture notes, roadmap, and changelog

## v1.0-dev.2 — Playback state and controls

- [x] Dedicated libmpv polling/event handling with safe cancellation
- [x] Observe core pause, time, duration, idle, load, and end state
- [x] Accurate position/duration slider with seeking
- [x] Play, pause, stop, relative seek, volume, and mute
- [x] File drag-and-drop
- [x] Media-open/native errors surfaced through status/logging
- [ ] Complete shortcut parity and frame-step polish

## v1.0-dev.3 — Tracks and chapters

- [x] Parse media tracks into Core models
- [x] Audio and subtitle track picker with readable labels
- [x] Subtitle Off state
- [x] Chapter parsing and timestamp navigation
- [x] Refresh tracks after media/title changes
- [x] Preserve active session selections where supported

## v1.0-dev.4 — Resume and session reliability

- [ ] Per-file watch-later checkpoints using the independent resume store
- [ ] Resume thresholds that avoid saving near the beginning or credits-end
- [ ] Restore prompt / automatic resume policy matching proven Parallax behavior
- [ ] Periodic crash-safe checkpoints
- [x] Window/appearance settings foundation
- [ ] Corrupt-state recovery UI and diagnostic export

## v1.0-dev.5 — DVD and Blu-ray workflows

- [x] Disc picker/detection with drive label, type, and readiness
- [x] DVD and Blu-ray device configuration through libmpv
- [x] Blu-ray main-feature playback and direct title switching
- [x] MakeMKV-backed title discovery
- [x] Protected Blu-ray LibMMBD compatibility path
- [x] DVD isolated playback host
- [x] DVD full-disc title mapping/reconciliation
- [x] Chapter/audio/subtitle navigation for active disc titles
- [ ] Persist disc mappings and user corrections
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

- [ ] Refined cinematic seek and volume sliders
- [ ] Premium command states and focus treatment
- [ ] Compact and expanded player layouts
- [ ] Now-playing/title-browser presentation polish
- [ ] High-DPI, ultrawide, multi-monitor, HDR, and VRR validation
- [ ] Accessibility names, keyboard focus, contrast, and screen-reader pass
- [x] New Parallax Studio application icon
- [ ] Final cross-suite branding and presentation polish

## v1.0-dev.8 — Screenshots, packaging, and hardening

- [ ] Screenshot command and configurable save folder
- [ ] Portable ZIP publish
- [ ] Optional self-contained Windows x64 publish
- [ ] Automated Core and Infrastructure tests
- [ ] Long-playback, repeated-open, disc, resume, and fullscreen soak tests
- [ ] Upgrade-safe settings schema and reproducible version stamping
- [ ] Third-party redistribution/license review for packaged native components
- [ ] Final README screenshots and user guide
- [ ] Application updater using the Typezer∅ Windows release standard

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
- Automatic metadata providers beyond the minimal disc-title enrichment needed for optical-media usability
