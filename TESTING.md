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
4. Reopen an already populated title browser and confirm cached metadata appears immediately. For a fresh uncached scan, confirm Parallax stops optical playback first to avoid drive contention, then discovers titles without starting one automatically.
5. Switch to a bonus title and verify tracks, subtitles, chapters, seeking, and fullscreen.

## DVD playback-host lifecycle checks

Run the automated host lifecycle checks on Windows with the .NET 10 SDK:

```powershell
.\scripts\Test-Infrastructure.ps1
.\scripts\Test-Infrastructure.ps1 -Configuration Release
```

They launch `ParallaxStudio.DvdHostStub`, a test-only stand-in that speaks the real
host protocol without loading libmpv, so no drive or disc is needed. They cover
repeated start/stop, hung requests and I/O-gate timeouts, hosts that ignore shutdown,
slow teardown, mid-session process death, cancelled and timed-out starts, and rapid
concurrent replacement through both `DvdPlaybackHostClient` and `MpvPlaybackService`.
They do not replace the physical DVD regression below.

During the physical DVD regression, also confirm:

1. Each title switch (C5 → stop → C1 → C2 … C4) succeeds on the first attempt.
   `parallax-studio.log` shows `Stopping DVD host session N … replaced by a new DVD
   host session` followed by `DVD host session N+1 connected`, with no
   `ObjectDisposedException` and no `stopped responding` entry.
2. Switching from DVD playback to a local file or Blu-ray works on the first attempt.
3. Closing Parallax Studio during DVD playback exits within about 5 seconds and
   leaves no `ParallaxStudio.DvdPlaybackHost.exe` in Task Manager.
4. A slow disc spin-up never produces `did not answer 'snapshot' within 10 s`. If it
   does, record the log section; the request timeout may need tuning for that drive.

## DVD full-title-map validation

1. Insert a known TV/movie DVD and browse titles.
2. On the Doctor Who regression disc, select C5 (44:36) first. The initial full-disc map may take several seconds.
3. Confirm C5 maps to the approximately 44:43 DVD program instead of incorrectly loading C1.
4. Stop and select C1 (1:07:26). Confirm it maps to the approximately 1:07:32 DVD program, including the zero-based first authored selector when required.
5. Try C2, C3, and C4 and confirm each selection keeps its own mapped runtime.
6. Verify pause, seek, tracks, subtitles, chapters, fullscreen, stop, and title switching.
7. Eject the disc and confirm the UI remains alive and clears stale optical-media state.

8. On the Doctor Who Complete Series 2018 D13 disc, expect `Full DVD title map built with 8 confident matches`, with metadata 3, 4 and 5 labelled `(calibrated)`. Every title must play and log `DVD title reconciled … matched metadata title N`. Also check the mapping in the log. Metadata title 6 must still map to DVD title 8, and no title may be forced into sequential order. Record the `DVD title-map inputs` line and any `not mapped (ambiguous|unmatched)` warnings.

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

Stage native playback dependencies before testing actual playback. The current
manifest pins libmpv `20261021-git-e471496c56` and its archive SHA-256. Use the
normal bootstrap and `scripts/Verify-NativeDependencies.ps1` to validate the
staged files; no alternate package or manual manifest override is needed.

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

Validate these cases in the current player:

- Launch with no media: the native video HWND is collapsed and the cinematic `PARALLAX / READY` landing state is visible.
- Open a local video: the landing state disappears, the native video surface appears, and playback remains hardware/native.
- Media Details shows video codec plus resolution/frame rate when mpv reports them, selected audio plus channel/sample-rate information, subtitle state, source type, duration, and chapter count.
- Change **Settings → Playback → Seek interval** between 5/10/15/30 seconds; both transport buttons and Left/Right arrow keys use the selected interval immediately.
- Enable **Resume playback**, play a local file beyond 30 seconds, close/stop Parallax, reopen the same file, and confirm it resumes near the saved position.
- A resume point within the final minute of a title is discarded so the next open starts fresh.
- Disable **Remember volume**, restart, and confirm Parallax starts at its neutral 68% level rather than restoring the previous session volume.
- Enable **Double-click for fullscreen** and confirm a double-click on the native video surface enters/exits fullscreen without interrupting playback.
- Re-run protected Blu-ray playback, Blu-ray title browsing, DVD isolated playback, DVD title reconciliation, track switching, chapter navigation, Cinema Auto-Expand, and fullscreen auto-hide regressions.

## Polish and intelligence v3 validation

Run the automated presentation checks from the repository root on Windows with
the .NET 10 SDK. The checks use generated image fixtures and synthetic metadata;
they do not require a media file, optical drive, protected disc, or a running mpv
playback session.

```powershell
.\scripts\Test-Presentation.ps1
.\scripts\Test-Presentation.ps1 -Configuration Release
dotnet build .\ParallaxStudio.sln -c Debug
dotnet build .\ParallaxStudio.sln -c Release
```

Then run these checks in the application with native dependencies staged:

1. **Now Playing and progress:** launch idle, open a local video, pause, seek,
   resume, stop, and reach the end. Check title hierarchy, PLAYING/PAUSED/loading
   state, elapsed/total time, remaining time, and the compact progress strip.
   An unknown runtime must show a loading placeholder and zero progress rather
   than a fabricated duration or a divide-by-zero value. Long titles must remain
   readable without pushing actions outside the card.
2. **Artwork order and fallback:** place distinctly identifiable images beside
   a video. Confirm same-basename art takes priority, then `poster`, then
   `folder`, then `cover`; each name supports `.jpg`, `.jpeg`, and `.png` in
   that extension order. Remove each winner and reopen the media to confirm the
   next candidate. Try corrupt images, an exclusively locked image, absent art,
   portrait art, and landscape art. Check graceful fallback, correct proportions,
   and that loaded covers can be replaced or deleted while playback continues.
3. **Source changes and failed opens:** switch rapidly between local files with
   different artwork and between a local file and a disc. A late image/metadata
   result from the previous source must not overwrite the new title. Attempt a
   file that disappears before opening and a disc/title that cannot be opened.
   If the prior media remains paused/playing, its presentation must recover;
   otherwise show the idle/error state without stale art or permanent loading.
4. **Details and badges:** verify the source/resolution/HDR/audio badges stay
   compact, and empty Media Details fields disappear. Confirm source labels,
   optical title numbers, runtime, selected audio/subtitles, and chapter count
   match playback. Use media whose decoder properties arrive after the first
   track list: resolution and PQ/HLG badges must update when reported. Do not
   infer HDR, Atmos, DTS-HD, or other profiles from a filename or generic codec.
   Unknown fields must remain absent while the loading status explains pending
   details.
5. **Optical transitions:** repeat protected Blu-ray playback and the DVD
   full-title-map checks above. Verify provisional/loading details clear on a
   title switch and update after runtime reconciliation. The displayed DVD title
   must retain the verified native program mapping; a UI refresh must not start
   a new title scan, reload a title, or change the selected playback program.
   Eject during loading and verify the app remains responsive.
6. **Appearance and persistence:** change Comfortable/Compact density and reduced
   motion while idle, playing, and paused. Check sidebar and transport spacing,
   button/dropdown/slider feedback, keyboard focus, and restart persistence.
   Reduced motion must affect decorative transitions while retaining clear
   control feedback. Enter fullscreen and verify floating controls, mouse
   movement, configured auto-hide timeout, cursor hiding, and Escape behavior
   continue to work in both densities and motion modes.
7. **Layout and theme coverage:** test 1380×860 and the 1020×680 minimum, all six
   themes and three typography profiles, then repeat at 125%, 150%, and 200%
   display scaling. Check Now Playing, artwork, wrapping badges, scrollable
   details/settings, and reachable transport actions. The native video surface
   must stay opaque and unobstructed; its ancestors must not acquire WPF bitmap
   effects or animated transforms.

Automated presentation checks and successful builds do not establish physical
disc compatibility. Protected Blu-ray playback, DVD isolation/title mapping,
disc changes, and fullscreen video still require the hardware regressions above.

## TMDB artwork validation

Run the deterministic TMDB resolver checks without a live API token:

```powershell
.\scripts\Test-TmdbArtwork.ps1
.\scripts\Test-TmdbArtwork.ps1 -Configuration Release
```

The checks cover disabled online artwork, missing credentials, cache hits, exact
title/year matching, ambiguous-title refusal, network failure, corrupt and
oversized poster responses, cancellation, and optical cache identity.

For live validation, set a TMDB API Read Access Token in
`PARALLAX_TMDB_TOKEN`, fully relaunch Parallax Studio, and verify:

1. A local movie with no sidecar artwork uses the built-in fallback immediately
   and then replaces it asynchronously with the TMDB poster.
2. Same-basename or `poster`/`folder`/`cover` local artwork still wins over cache
   and TMDB.
3. `Blade Runner 2049 [2017]` resolves by exact title and bracketed year.
4. A `They Live` Blu-ray can receive a poster without reading artwork from the
   physical disc; eject/reinsert should reuse the Parallax-owned cache.
5. Moving the same optical title between drive letters does not split its poster
   cache identity.
6. An ambiguous title-only query is refused instead of showing a guessed poster.
7. Disable **Online posters** and confirm no TMDB artwork is shown or requested.
8. Disable **Cache downloaded posters** and confirm live lookup still works but
   no new cache file is retained.
9. Rapidly switch sources while a lookup is in flight; the existing media
   generation guard must prevent the prior result from replacing the newer
   source's artwork.
10. Re-run protected Blu-ray, isolated DVD, local playback, fullscreen, density,
    and reduced-motion regressions. The TMDB service must not alter playback.
