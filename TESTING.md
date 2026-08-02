# Parallax Studio v1.0-dev.5.5.3.1 Test

## DVD detection and direct playback

1. Start Parallax Studio with a DVD inserted.
2. Confirm the optical card identifies the disc as **DVD** automatically.
3. Choose **Play longest title**.
4. Confirm video and audio begin without opening the DVD menu.
5. Open **Tracks & Chapters** and verify audio, subtitles, and chapters populate.
6. Switch audio/subtitle tracks and jump between chapters.
7. Enter fullscreen, press **Esc**, and confirm the track panel and taskbar layout remain correct.

## DVD title and episode discovery

1. Stop playback and choose **Browse titles**.
2. Confirm discovery does not auto-play the disc and the window remains responsive.
3. Confirm **Cancel scan** is available.
4. Allow discovery to complete.
5. On a TV-series DVD, confirm multiple episode-length titles are labeled **EPISODE CANDIDATE** rather than forcing one main feature.
6. Select an episode or bonus item and choose **Play selected title**.
7. Confirm tracks, subtitles, chapters, duration, and timeline refresh.
8. Reopen **Browse titles** and confirm cached metadata appears without another full scan.

## Blu-ray regression

1. Insert a Blu-ray and verify **Play main feature** and **Browse titles** still appear.
2. Confirm protected playback still uses MakeMKV LibMMBD.
3. Confirm title discovery, track selection, chapters, and bonus-title playback still work.

## Disc swap

1. Eject the DVD while Parallax remains open.
2. Confirm the optical panel changes to no media.
3. Insert a Blu-ray, then swap back to the DVD.
4. Confirm the label, disc kind, buttons, and cached title list never leak between discs.

## DVD crash-isolation test

1. Run `./build.ps1 -Bootstrap -InstallBootstrapTools -Run`.
2. Confirm bootstrap reports `Installed libdvdcss 1.5.0` or that 1.5.0 is already present.
3. Insert a DVD and choose **Play longest title**.
4. Parallax runs the selected DVD source in `dvd-probe\ParallaxStudio.DvdProbe.exe` first.
5. If the helper fails or terminates natively, Parallax must remain open and show a warning with the probe exit code.
6. If the helper passes, the same source is submitted to the embedded player.
7. Repeat with one episode candidate from **Browse titles**.


## v1.0-dev.5.5.3 isolated DVD host test

1. Build with `./build.ps1 -Bootstrap -InstallBootstrapTools -Run`.
2. Confirm the console prints `Isolated DVD playback host staged:`.
3. Insert the Doctor Who DVD and open **Browse titles**.
4. Select the same episode-length title that terminated v1.0-dev.5.5.2.
5. Confirm the Parallax Studio window remains open while the title loads.
6. Confirm video renders inside the normal cinema surface, not a separate player window.
7. Test pause, seek, volume, mute, audio, subtitles, and chapters.
8. Switch to another DVD title and confirm the old host is replaced cleanly.
9. Eject the DVD and open a Blu-ray or local file to confirm the established playback path still works.
10. If the child host fails, capture both `parallax-studio.log` and the lines prefixed `DVD host:`.
