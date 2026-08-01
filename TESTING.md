# Parallax Studio v1.0-dev.5.1.4.1 Visible AACS Warning Hotfix

1. Run `./build.ps1 -Bootstrap -InstallBootstrapTools -Run`.
2. Keep `%APPDATA%\aacs\KEYDB.cfg` empty.
3. Press **Play main feature**.
4. Confirm a visible warning dialog appears describing the empty KEYDB file.
5. Press **Browse titles** and confirm the same readiness warning appears.


## v1.0-dev.5.4 track-state refresh
1. Start Blu-ray playback and confirm audio/subtitle tracks populate.
2. Enter fullscreen, press Esc, then open Tracks & Chapters immediately.
3. Confirm audio tracks do not disappear and the active audio badge is present without selecting a subtitle first.
4. Switch audio and subtitles repeatedly; ACTIVE must move immediately and remain correct.
5. Repeat fullscreen -> Esc -> Tracks several times.
