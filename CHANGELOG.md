## 1.0-dev.5.3.1.1

- Fixed WPF DataTemplate trigger placement in the polished audio, subtitle, and chapter cards so the presentation pass compiles correctly.

## 1.0-dev.5.3.1

- Refined audio and subtitle labels into readable language, codec, channel, and role descriptions.
- Added stronger active-track highlighting with a dedicated ACTIVE badge.
- Added polished chapter rows with compact timestamps and a NOW PLAYING badge.
- Preserved live Blu-ray audio, subtitle, and chapter selection behavior.

## 1.0-dev.5.3

- Automatically stages MakeMKV `libmmbd64.dll` as runtime `libaacs.dll` and `libbdplus.dll` after each successful build.
- Preserves the standard bootstrap libraries as `libaacs.standard.dll` and `libbdplus.standard.dll` in build output.
- Verifies all staged provider files with SHA-256 before launch.
- Reports whether LibMMBD runtime aliases are actually active instead of merely detected.

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

## v1.0-dev.5.1.4.1

- Show a visible modal warning when Blu-ray playback is blocked by a missing or empty AACS `KEYDB.cfg`.
- Apply the same readiness warning to both main-feature playback and title browsing.
- Keep the detailed reason in the persistent playback status text.

## v1.0-dev.5.1.4

- Added Blu-ray readiness checks for missing or empty `KEYDB.cfg`.
- Surface libmpv/AACS failures in the app status bar, including no matching processing key.
- Stop logging Blu-ray requests as opened before libmpv confirms successful loading.
- Preserve detailed native-library and libmpv diagnostics.

## v1.0-dev.5.1.3

- Capture libmpv log messages and asynchronous load failures.
- Log Blu-ray source, drive, native disc libraries, and AACS key database presence.
- Surface failed Blu-ray loads in the player status bar.
- Correct diagnostics path documentation to `%LOCALAPPDATA%\ParallaxStudio\logs`.
