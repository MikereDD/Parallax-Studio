# Optical Media Architecture

This document records the current Parallax Studio DVD/Blu-ray playback design and the boundaries around third-party native components.

## Blu-ray path

Parallax Studio uses libmpv/libbluray for Blu-ray playback. The application configures the optical drive as the Blu-ray device and uses libmpv disc sources for main-feature and direct-title playback.

Title discovery is performed separately through a locally installed MakeMKV command-line executable when available. Discovery data is used for duration, chapter count, playlist/title metadata, likely-main-feature ranking, and the title browser. Discovery is serialized with playback access so MakeMKV and libmpv do not compete for the optical drive.

### Protected Blu-ray provider

The preferred development path for protected Blu-rays is a locally installed MakeMKV `libmmbd64.dll`.

At build/run time Parallax checks, in order:

1. `PARALLAX_STUDIO_LIBMMBD`, when explicitly configured;
2. the standard 32-bit Program Files MakeMKV installation directory;
3. the standard Program Files MakeMKV installation directory.

When a local provider is found, `build.ps1` stages the user's `libmmbd64.dll` into the application output and creates runtime compatibility aliases named `libaacs.dll` and `libbdplus.dll`. If the standard bootstrapped providers exist, they are preserved alongside the runtime as `libaacs.standard.dll` and `libbdplus.standard.dll`.

The staging step verifies that the alias files match the local `libmmbd64.dll` before launch and records the provider path and SHA-256 in a `libmmbd.staged` marker beside the aliases. While that marker exists, a plain `dotnet build` never overwrites the aliases with the standard libraries; removing the marker (or running `dotnet clean`) returns the next build to the standard provider. `scripts/Test-ProviderStaging.ps1` checks that the staged provider matches the marker. The playback service also sets process-local `LIBAACS_PATH` and `LIBBDPLUS_PATH` to the selected provider and records the active provider in the log.

If MakeMKV is unavailable, Parallax falls back to the standard bootstrapped `libaacs`/`libbdplus` path. Protected-disc success then depends on that provider's normal configuration and the specific disc.

## DVD path

DVD playback uses libmpv/libdvdnav and the pinned x64 `libdvdcss-2.dll` package for CSS-protected discs.

Earlier in-process DVD tests exposed native failures capable of terminating the WPF application. The production direction therefore isolates DVD playback in a dedicated child process:

```text
ParallaxStudio.App
    │
    ├── private named-pipe control/state IPC
    │
    └── ParallaxStudio.DvdPlaybackHost.exe
            └── libmpv / libdvdnav / libdvdcss
```

The host renders into Parallax Studio's native video surface while the main WPF process remains responsible for the surrounding UI. A DVD-host failure can be reported/restarted without taking down the main application.

A separate DVD probe is retained for bounded native inspection and full title-map construction.

## DVD title reconciliation

DVD authoring is inconsistent. MakeMKV discovery indices and libdvdnav `dvd://N` selectors can refer to different authored programs, and discs can expose aggregate/menu/technical titles in addition to episodes or features.

Parallax Studio therefore does not permanently assume that metadata title N equals DVD program N. v1.0-dev.5.5.5 reads the libdvdnav edition table and compares actual program runtimes against MakeMKV discovery metadata. Confident matches form a per-disc title map. Unknown/ambiguous selections are rejected rather than silently loading a different program.

A match is accepted only when it is the only consistent candidate in both directions: the metadata title has exactly one consistent DVD title, and that DVD title has exactly one consistent metadata title. "Closest" is never evidence. Title or selector order and forced one-to-one elimination are never used to break a tie. Matching runs in two stages:

1. **Direct.** Runtimes within 15 s (`DvdTitleMatcher.RuntimeWindow`). The unique direct matches become *anchors*.
2. **Calibrated.** Runs only if titles are still unresolved, and only with at least 3 anchors, 2 of them 10 minutes or longer. Parallax computes the runtime ratios `r` that explain every anchor at once (`dvd = metadata × (1 + r) ± 3 s`). The remaining titles are then judged against the range that ratio predicts for their own length. If no single ratio fits all anchors, calibration is not used.

On the Doctor Who 2018 D13 disc, the discrepancy grows with length: +1 s at 0:24, +5 to +7 s at 42–67 min, and +29 s at 4:00. The fixed window therefore leaves the 41:59 / 41:47 pair ambiguous and the 4-hour aggregate unmatched. The disc's five direct anchors calibrate `r` to about 0.15–0.22 %. That resolves both, and rejects the swapped assignment, which would need −6 s and +17 s offsets.

Titles that remain unresolved are logged as `not mapped (ambiguous)` or `not mapped (unmatched)`, with the competing or nearest titles. Selecting one shows the reason (`OpticalTitleUnavailableException`) instead of a playback error. Calibrated matches are labelled `(calibrated)` in the map log, and the calibration itself is logged.

After a title loads, its runtime (judged against the same calibration when one exists) and chapter count (±1 chapter) are checked with the same rule:

- **Unique and agreeing:** the mapping is verified.
- **Ambiguous:** the existing mapping is kept, unverified.
- **Unique but disagreeing:** both claims are withdrawn, and logged as a reconciliation conflict.

Future work will persist verified mappings between sessions and add optional metadata enrichment for discs that do not contain useful title names.

## Disc identity and cache direction

All session-scoped optical caches (Blu-ray/DVD title lists, libbluray edition lists, DVD title maps and verified mappings) are keyed by one identity builder, `OpticalDiscIdentity`, with three classes:

- **Strong** — built from the disc itself: disc kind, normalized volume label, and SHA-256 hashed structural evidence (`VIDEO_TS\*.IFO` names/sizes/timestamps/hashes for DVD; `BDMV\index.bdmv` and `MovieObject.bdmv` hashes plus the sorted playlist filename/size fingerprint for Blu-ray). Large stream files (VOB, `.m2ts`) are never read. A strong identity is independent of the drive letter, so the same physical disc keeps its caches when moved between drives, and two discs that merely share a volume label cannot collide. Timestamps are included only as secondary evidence alongside content hashes.
- **Degraded** — when the mandatory identity file (`VIDEO_TS.IFO` / `index.bdmv`) is unreadable but some trustworthy evidence remains (partial structural fingerprints, reported volume size), the identity keeps all of that evidence but is scoped to the drive root. It never becomes portable across drives.
- **Insufficient** — when no distinguishing evidence is readable at all, the service mints a fresh, non-reusable key per establishment so stale title or mapping caches can never be hit across a media transition that left no evidence.

Identity establishment is logged once per session (strong, degraded, or insufficient) with a short fingerprint prefix. Persistent mapping (future work) should reuse the strong identity and continue to avoid assuming volume labels encode a literal disc number.

## Diagnostics

Runtime diagnostics are written to:

```text
%LOCALAPPDATA%\ParallaxStudio\logs\parallax-studio.log
```

Useful messages include the detected disc-decryption provider, native library paths, MakeMKV discovery state, DVD host lifecycle, edition/title tables, playback load events, and title reconciliation results.

## Distribution boundary

The Parallax Studio source repository does not fetch MakeMKV. MakeMKV is an external user-installed application. Local development builds may copy files from that installation into their local build output. Release packaging must not assume those staged files may be redistributed; third-party licensing and distribution requirements must be reviewed before any public binary package includes them.
