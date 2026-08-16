## 1.0-dev.5.5.5 — Full DVD Title Map

- probes the DVD edition table once before the first selected-title playback
- matches all usable MakeMKV entries to libdvdnav selectors by runtime
- supports the zero-based `dvd://0` selector required for the first authored title
- refuses to launch an unmapped entry instead of silently playing the wrong program
- keeps isolated-host reconciliation as a post-load verification step

## 1.0-dev.5.5.4.2 — DVD Reconciliation State-Event Hotfix

- Refreshes the DVD title browser from the authoritative reconciled playback-state event.
- Moves PLAYING and selection to the verified metadata title after runtime/chapter matching.
- Removes dependence on the short initial-load retry window that could expire before the UI consumed the mapping.
- Logs `DVD reconciliation UI applied` when the verified mapping reaches the title panel.

## 1.0-dev.5.5.4.2 — DVD Reconciliation UI Refresh Hotfix

- Keeps title refresh polling alive while a DVD entry is still using the provisional numeric mapping.
- Waits for the playback service to expose a verified runtime/chapter match before completing the title-switch workflow.
- Moves selection and the PLAYING badge to the reconciled metadata entry as soon as the verified mapping appears.
- Preserves playback when an unusual disc cannot be matched confidently; the UI falls back to the provisional entry after the bounded retry window.
- Retains the proven isolated DVD playback host and v1.0-dev.5.5.4 matching algorithm unchanged.

## 1.0-dev.5.5.4 — DVD Title Reconciliation

- Matches the actual isolated-host DVD runtime and chapter count back to MakeMKV discovery metadata.
- Stops assuming that a MakeMKV title index is the same as libdvdnav `dvd://N`.
- Learns verified per-disc mappings in memory as titles are played.
- Reuses learned mappings when the matched title is selected again.
- Moves the PLAYING badge and selected title to the entry that actually loaded.
- Shows the verified DVD program number in title details and the discovery summary.
- Keeps the isolated DVD playback architecture introduced in v1.0-dev.5.5.3.

## 1.0-dev.5.5.3.1 — Isolated DVD Host Build Hotfix

- Fixed the `Task<MediaTrack[]>` to `Task<IReadOnlyList<MediaTrack>>` compile mismatch in the DVD host client.
- Fixed the matching chapter-list return-type mismatch.
- Preserved the isolated DVD playback-host implementation from v1.0-dev.5.5.3 unchanged for runtime testing.

## 1.0-dev.5.5.3

## 1.0-dev.5.5.3 — Isolated DVD Playback Host

- Moved DVD playback out of the WPF process into `ParallaxStudio.DvdPlaybackHost.exe`.
- Added named-pipe IPC for playback state, pause, stop, seeking, volume, mute, tracks, subtitles, and chapters.
- Renders the isolated DVD engine into the existing Parallax video surface.
- Preserves Blu-ray and local-file playback on the established in-process libmpv path.
- Contains native DVD failures inside the child host so the main Parallax Studio window remains open.
- Automatically terminates and replaces the DVD host when switching titles, media types, or closing the app.

- Replaces the legacy 2013 libdvdcss 1.2.12 Windows binary with pinned libdvdcss 1.5.0 from the MSYS2 x64 package built from VideoLAN source.
- Adds an isolated DVD runtime probe process so a native libmpv/DVD failure cannot terminate the Parallax Studio UI process.
- Probes the exact longest-title or numbered-title URL before submitting it to the embedded player.
- Shows a visible safety warning and preserves the app when the probe fails, including the native exit code in diagnostics.
- Removes the experimental DVD edition-switch workaround; selected titles use the exact `dvd://N` source only after the isolated probe succeeds.

## 1.0-dev.5.5.1

- Replaced direct `dvd://N` selected-title loading with a two-stage safe DVD load followed by mpv `edition` selection.
- Keeps playback state in Loading until the final DVD title is loaded and defers track/chapter polling during the transition.
- Validates cached DVD title selection and hides aggregate navigation entries longer than 12 hours.
- Uses real DVD title names when MakeMKV exposes them.

- Adds direct DVD playback through libmpv using `dvd://` and the detected optical drive.
- Adds direct numbered DVD-title playback with audio, subtitle, chapter, timeline, and fullscreen reuse.
- Extends serialized, cancellable title discovery to DVDs through the installed MakeMKV command-line engine.
- Adds DVD-aware title classification for episode-length programs, long features, bonus features, and short menu/intro items.
- Keeps discovery from auto-starting playback and reuses per-disc cached title metadata.
- Adds DVD-specific optical-card actions and corrects Blu-ray-only help/status text.
- Verifies `libdvdcss-2.dll` in the runtime output after each successful build.

## 1.0-dev.5.4.2.1

- Makes Blu-ray title discovery responsive by removing automatic playback from fresh title scans.
- Serializes MakeMKV discovery and libmpv disc-open requests so they do not compete for the optical drive.
- Adds a visible **Cancel scan** action that terminates the MakeMKV process tree and releases the drive.
- Batches title results into the WPF list only after discovery completes, avoiding repeated layout churn.
- Adds automatic optical-media change detection with a two-second lightweight watcher.
- Clears stale title state when a disc is ejected or replaced and automatically recognizes the newly inserted disc.
- Keys the in-memory title cache by disc identity instead of drive letter alone.
- Keeps manual **Scan** as a fallback while normal disc swaps update automatically.

## 1.0-dev.5.4.2

- Fix MakeMKV title discovery after switching titles by retaining the actual Blu-ray device path.
- Keep maximized/restored custom-chrome windows inside the monitor work area above the taskbar.

## 1.0-dev.5.3.1.2

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

## v1.0-dev.5.3.1.2

- Fixed the native video surface overlapping and clipping the Tracks, Subtitles & Chapters panel after exiting fullscreen.
- Restores the normal sidebar column to Auto sizing, forces a fresh WPF measure/arrange pass, and momentarily hides the native video host while its child HWND is resized.
- Preserves the previous normal or maximized window state after fullscreen.

## 1.0-dev.5.4.1
- Added MakeMKV-backed Blu-ray title discovery with duration, chapter count, playlist metadata, likely-main-feature ranking, duplicate-duration hints, loading state, and direct title playback.

## v1.0-dev.5.4.1
- Preserve title selection when discovery refreshes.
- Reopen title browser without restarting the current disc.
- Keep the browser open during direct title switches and mark the active title.
- Retry title-state detection after switching and refresh tracks, subtitles, chapters, duration, and timeline.
- Clarify that main-feature playback continues while MakeMKV discovers titles.

## 1.0-dev.5.4.2
- Cache MakeMKV title metadata per Blu-ray disc so title switching and browser reopening do not rescan the physical disc.
- Add useful fallback names and classifications for bonus features, deleted-scene/featurette candidates, short bonus items, and technical intro/warning items.
- Preserve direct title playback, active-title state, tracks, subtitles, chapters, and work-area fixes.
