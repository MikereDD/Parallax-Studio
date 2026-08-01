# Changelog

## v1.0-dev.4.3

- Replaced native light WPF selectors with a complete Studio-themed ComboBox.
- Added premium themed tabs, list items, scrollbars, selected states, hover states, and disabled states.
- Removed white fallback surfaces from the Tracks, Subtitles, and Chapters panel.
- Added readable empty states for media without alternate tracks, subtitles, or chapters.
- Increased the media panel width and improved spacing and contrast across all current themes.
- Preserved the validated native libmpv bootstrap and dev.4 playback features.

## v1.0-dev.4.2.3

- Simplified native dependency bootstrap to run directly from `build.ps1`.
- Removed helper-script `exit` calls that could terminate the parent build.
- Prefer `C:\Program Files\7-Zip\7z.exe` before PATH shims.
- Added visible startup stages and explicit archive/DLL validation.
- Use direct 7-Zip invocation with reliable PowerShell argument handling.

## v1.0-dev.4.2.3

- Run native dependency bootstrap in an isolated PowerShell process.
- Add explicit bootstrap stage output.
- Add `native/bootstrap.log` with persistent diagnostics.
- Prevent child-script flow control from terminating the parent build silently.

## v1.0-dev.4.1

- Fixed the `MediaChapter` timestamp format string so the Core project compiles correctly.

## v1.0-dev.4

- Added audio-track discovery and live switching.
- Added embedded subtitle discovery with a clear Subtitles Off entry.
- Added external subtitle loading.
- Added subtitle delay controls in 0.1-second increments.
- Added chapter discovery, current-chapter indication, direct chapter selection, and previous/next chapter commands.
- Added a premium tracks/subtitles/chapters side panel.
- Added basic media summary counts to Now Playing.
- Preserved seeking, drag-and-drop, mute, fullscreen, timeline tracking, and clean shutdown.

## v1.0-dev.4.2.3

- Add a pinned native dependency manifest for the libmpv Windows x64-v3 build.
- Add explicit bootstrap and verification scripts for `libmpv-2.dll`.
- Add `build.ps1 -Bootstrap` and optional 7-Zip installation support.
- Keep libdvdcss and libaacs optional and user-supplied.
- Record resolved archive and DLL SHA-256 values in a generated lock file.
