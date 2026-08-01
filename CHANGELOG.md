# Changelog

## v1.0-dev.5.1.2

- Expanded first-run native bootstrap to install libmpv, libaacs, libbdplus, and libdvdcss.
- Pinned the libaacs/libbdplus binary release tag and retained a local SHA-256 lock manifest.
- Made optional disc-library download failures warning-only so ordinary playback builds remain usable.
- Preserved the docked Blu-ray title browser layout from dev.5.1.1.


## v1.0-dev.5.1.1

- Fixed the Blu-ray title browser being obscured by the native libmpv video HWND.
- Docked Blu-ray and Tracks panels beside the video surface instead of layering WPF content over it.
- Made the right column size to the active panel and temporarily hide the normal sidebar while a navigation panel is open.
- Preserved video aspect ratio and playback while browsing disc titles.

## v1.0-dev.5.1.1.1

- Added a direct Blu-ray title browser backed by libmpv disc-title properties.
- Added manual playback of numbered Blu-ray titles.
- Added clearer main-feature and title-browsing actions in the optical-media card.

- Added first Blu-ray disc playback foundation.
- Added a Play Blu-ray action for detected ready Blu-ray drives.
- Configures libmpv `bluray-device` and opens `bd://longest` for direct main-feature testing.
- Reuses track, subtitle, chapter, timeline, volume, fullscreen, and clean-shutdown systems during disc playback.
- Adds explicit protected-disc/AACS diagnostic messaging without downloading decryption keys.

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
