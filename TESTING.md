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
