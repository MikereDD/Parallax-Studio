# Parallax Studio v1.0-dev.5 Blu-ray Foundation Test

1. Run `./build.ps1 -Bootstrap -InstallBootstrapTools -Run`.
2. Insert a Blu-ray disc into the optical drive.
3. Select **Scan** if the disc was inserted after launch.
4. Confirm the card identifies **Blu-ray** and shows **Play Blu-ray**.
5. Select **Play Blu-ray**. The build asks libmpv/libbluray to open `bd://longest` from that drive.
6. Confirm video/audio begins and the timeline obtains a duration.
7. Open **Tracks** and test audio, subtitles, and chapters.
8. Close the app and confirm playback and the process stop completely.

If playback fails, copy the status text and the latest log from `%LOCALAPPDATA%\Parallax Studio\logs`. Commercial protected discs may require user-supplied AACS keys/configuration; this build does not download or distribute decryption material.
