# Parallax Studio v1.0-dev.5.5.5 Test Guide

## Build/runtime sanity

1. Run `./build.ps1 -Bootstrap -InstallBootstrapTools -Run`.
2. Confirm the build reports the native dependency verification result.
3. When MakeMKV is installed, confirm the build reports `MakeMKV LibMMBD staged for protected Blu-ray playback` and the app log reports `MakeMKV LibMMBD active`.
4. Confirm `%LOCALAPPDATA%\ParallaxStudio\logs\parallax-studio.log` is being written.

## Blu-ray regression

1. Insert a known protected Blu-ray.
2. Confirm the disc is detected automatically.
3. Play the main feature and confirm video/audio load without an AACS failure.
4. Browse titles and confirm discovery does not interrupt currently playing content.
5. Switch to a bonus title and verify tracks, subtitles, chapters, seeking, and fullscreen.

## DVD full-title-map validation

1. Insert a known TV/movie DVD and browse titles.
2. On the Doctor Who regression disc, select C5 (44:36) first. The initial full-disc map may take several seconds.
3. Confirm C5 maps to the approximately 44:43 DVD program instead of incorrectly loading C1.
4. Stop and select C1 (1:07:26). Confirm it maps to the approximately 1:07:32 DVD program, including the zero-based first authored selector when required.
5. Try C2, C3, and C4 and confirm each selection keeps its own mapped runtime.
6. Verify pause, seek, tracks, subtitles, chapters, fullscreen, stop, and title switching.
7. Eject the disc and confirm the UI remains alive and clears stale optical-media state.

Expected DVD log anchors include:

```text
Full DVD title map built with ... confident matches
DVD title reconciled: ...
```

## Premium cinematic shell regression

Build both configurations before running the UI checks:

```powershell
dotnet build .\ParallaxStudio.sln -c Debug
dotnet build .\ParallaxStudio.sln -c Release
```

The native inputs required by the DVD helper projects must be staged first. The
manifest's libmpv `20260421` upstream release is currently unavailable (404).
For this shell's local validation, the official `20261021-git-e471496c56` build was
used with checksum verification; the dependency manifest was left unchanged.

1. At 1380×860 and 1020×680, cycle through OLED Black, Dark Monochrome, Graphite,
   Midnight Indigo, Aurora Pulse, and Obsidian Gold, then cycle back. Check the
   window lighting, card rims, timeline, focus rings, and active badges update
   immediately. Repeat with Cadence, Precision, and Clear typography.
2. Tab through selectors, buttons, timeline, and volume. Verify visible focus,
   hover, pressed, and disabled states, including readable text on primary
   buttons. Use arrow keys and type-to-select in theme selectors; these inputs
   must not seek, change volume, mute, or enter fullscreen.
3. Open a local video and verify video/audio, play/pause, stop, seeking, volume,
   mute, file drop, and existing keyboard shortcuts. Click both ends of the
   timeline, drag the thumb, and use its arrow/Home/End/Page keys. The thumb must
   follow the pointer while dragging and commit the seek on release.
4. Enter and leave fullscreen from normal and maximized windows. Confirm the
   video fits its host, cinema heading/footer hide in fullscreen, and sidebar,
   title bar, window bounds, and margins return correctly.
5. With multiple optical drives/discs, scroll the sidebar at minimum window
   size. Confirm scan and both disc actions remain reachable. Browse titles
   during playback, cancel discovery, and select a title. Check long names and
   classification labels wrap, and the active title has an accent border and a
   readable PLAYING badge.
6. Open Audio, Subtitles, and Chapters. Check long stream labels, active badges,
   scrolling, external subtitle loading, subtitle delay, and chapter changes.
   Close each panel and confirm the sidebar returns without covering video.
7. Repeat resize, panel, and focus checks at 125%, 150%, and 200% display scaling.
   Check the native video stays opaque and unobstructed; its ancestors must not
   acquire WPF bitmap effects or animated transforms.

Automated offscreen shell checks cover theme switching, minimum/default layout,
synthetic disc/title/track/chapter states, resource/binding errors, and rendered
previews. They do not replace the physical Blu-ray/DVD regressions above.
## Player Experience v2 validation

Validate these cases after applying the Player Experience v2 overlay:

- Launch with no media: the native video HWND is collapsed and the cinematic `PARALLAX / READY` landing state is visible.
- Open a local video: the landing state disappears, the native video surface appears, and playback remains hardware/native.
- Media Details shows video codec plus resolution/frame rate when mpv reports them, selected audio plus channel/sample-rate information, subtitle state, source type, duration, and chapter count.
- Change **Settings → Playback → Seek interval** between 5/10/15/30 seconds; both transport buttons and Left/Right arrow keys use the selected interval immediately.
- Enable **Resume playback**, play a local file beyond 30 seconds, close/stop Parallax, reopen the same file, and confirm it resumes near the saved position.
- A resume point within the final minute of a title is discarded so the next open starts fresh.
- Disable **Remember volume**, restart, and confirm Parallax starts at its neutral 68% level rather than restoring the previous session volume.
- Enable **Double-click for fullscreen** and confirm a double-click on the native video surface enters/exits fullscreen without interrupting playback.
- Re-run protected Blu-ray playback, Blu-ray title browsing, DVD isolated playback, DVD title reconciliation, track switching, chapter navigation, Cinema Auto-Expand, and fullscreen auto-hide regressions.
