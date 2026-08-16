# Parallax Studio v1.0-dev.5.5.5 Test

1. Run `./build.ps1 -Bootstrap -InstallBootstrapTools -Run`.
2. Browse the Doctor Who DVD titles.
3. Select C5 (44:36) first. The initial full-disc map may take several seconds.
4. Confirm C5 plays the 44:43 DVD title rather than C1.
5. Stop and select C1 (1:07:26). Confirm it loads the 1:07:32 DVD title through `dvd://0`.
6. Try C2, C3, and C4 and confirm each title keeps its own runtime.
7. Verify pause, seek, tracks, subtitles, chapters, and fullscreen.

Expected log anchor:

`Full DVD title map built with ... confident matches`
