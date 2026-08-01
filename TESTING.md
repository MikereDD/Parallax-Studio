# Parallax Studio v1.0-dev.5.4.2.1 Test

## Responsive title discovery

1. Start Parallax Studio with a Blu-ray inserted.
2. Choose **Browse titles** without starting playback first.
3. Confirm the window remains responsive while the drive is scanned.
4. Confirm the movie does not auto-play during discovery.
5. Confirm **Cancel scan** is visible and cancels promptly.
6. Run discovery again and allow it to finish.
7. Select a title and choose **Play selected title**.
8. Confirm audio, subtitle, chapter, duration, and timeline state refresh after playback begins.

## Disc-swap watcher

1. Eject the current disc while Parallax remains open.
2. Confirm the optical panel changes to no media within a few seconds.
3. Insert a different Blu-ray.
4. Confirm its label appears automatically without pressing **Scan**.
5. Browse titles and verify no title metadata from the prior disc remains.
6. Reinsert the first disc and confirm its own cached metadata is used only for that disc.

## Contention regression

- While a Blu-ray is playing, choose **Browse titles** for a disc without cached metadata.
- Confirm Parallax stops disc playback before beginning discovery rather than running MakeMKV and libmpv against the drive simultaneously.
- Confirm the UI remains responsive throughout.
