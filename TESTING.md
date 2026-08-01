# Parallax Studio v1.0-dev.4.1 Test Checklist

## Setup

1. Copy the x64 `libmpv-2.dll` into `native\`.
2. Run `./build.ps1 -Run`.
3. Open an MKV with multiple audio tracks, subtitles, and chapters.

## Test

- Open **Tracks** and confirm the panel appears.
- Audio tab: verify all audio streams appear and switch between at least two.
- Subtitles tab: switch embedded subtitles, choose **Subtitles Off**, then turn subtitles back on.
- Load an external `.srt` or `.ass` file.
- Adjust subtitle delay backward and forward.
- Chapters tab: select a chapter and test Previous/Next chapter.
- Verify video, seeking, volume, mute, fullscreen, drag-and-drop, and clean app exit still work.

Report compile errors, missing track metadata, incorrect selection state, crashes, or audio continuing after close.
