using ParallaxStudio.Core.Enums;
using ParallaxStudio.Core.Models;
using ParallaxStudio.Infrastructure.Playback;

namespace ParallaxStudio.Infrastructure.Tests;

/// <summary>
/// Phase 5: a discovered optical title may only report IsActive while playback is
/// genuinely live. StopAsync retains the previous Source on the stopped snapshot, so
/// gating on source alone left a stale PLAYING badge after stop, fault, or a switch to
/// a local file. HasLiveOpticalPlayback is the single gate used by both
/// GetBluRayTitlesAsync and GetDvdTitlesAsync.
/// </summary>
internal static partial class Program
{
    private static (string Name, Func<Task> Run)[] OpticalActiveStateChecks =>
    [
        ("optical: playing disc playback reports live", Sync(PlayingDiscIsLive)),
        ("optical: loading and paused disc playback report live", Sync(LoadingAndPausedDiscAreLive)),
        ("optical: stopped snapshot with a retained disc source is not live", Sync(StoppedDiscIsNotLive)),
        ("optical: faulted and idle snapshots are not live", Sync(FaultedAndIdleAreNotLive)),
        ("optical: local-file playback never marks a disc title live", Sync(LocalFileIsNotLive)),
        ("optical: null source is not live", Sync(NullSourceIsNotLive)),
        ("optical: source prefix match is case-insensitive and disc-specific", Sync(PrefixMatching))
    ];

    private static PlaybackSnapshot Snapshot(PlaybackState state, string? source) =>
        new(state, source, "Title", TimeSpan.FromMinutes(1), TimeSpan.FromHours(2), 68, false);

    private static void PlayingDiscIsLive()
    {
        Check(MpvPlaybackService.HasLiveOpticalPlayback(
                Snapshot(PlaybackState.Playing, "Blu-ray disc • E:\\"), "Blu-ray disc"),
            "Playing Blu-ray playback must be live");
        Check(MpvPlaybackService.HasLiveOpticalPlayback(
                Snapshot(PlaybackState.Playing, "DVD disc • D:\\ • DVD title 3"), "DVD disc"),
            "Playing DVD playback must be live");
    }

    private static void LoadingAndPausedDiscAreLive()
    {
        Check(MpvPlaybackService.HasLiveOpticalPlayback(
                Snapshot(PlaybackState.Loading, "Blu-ray disc • E:\\"), "Blu-ray disc"),
            "Loading disc playback must be live so a reopening title stays marked");
        Check(MpvPlaybackService.HasLiveOpticalPlayback(
                Snapshot(PlaybackState.Paused, "DVD disc • D:\\"), "DVD disc"),
            "Paused disc playback must be live");
    }

    private static void StoppedDiscIsNotLive()
    {
        // StopAsync keeps Source while setting State=Stopped; this is the regression case
        // that left a stale PLAYING badge in the title browser.
        Check(!MpvPlaybackService.HasLiveOpticalPlayback(
                Snapshot(PlaybackState.Stopped, "Blu-ray disc • E:\\"), "Blu-ray disc"),
            "A stopped snapshot must not mark a Blu-ray title live even though Source remains");
        Check(!MpvPlaybackService.HasLiveOpticalPlayback(
                Snapshot(PlaybackState.Stopped, "DVD disc • D:\\ • DVD title 3"), "DVD disc"),
            "A stopped snapshot must not mark a DVD title live even though Source remains");
    }

    private static void FaultedAndIdleAreNotLive()
    {
        Check(!MpvPlaybackService.HasLiveOpticalPlayback(
                Snapshot(PlaybackState.Faulted, "Blu-ray disc • E:\\"), "Blu-ray disc"),
            "A faulted snapshot must not be live");
        Check(!MpvPlaybackService.HasLiveOpticalPlayback(
                Snapshot(PlaybackState.Idle, "DVD disc • D:\\"), "DVD disc"),
            "An idle snapshot must not be live");
    }

    private static void LocalFileIsNotLive()
    {
        Check(!MpvPlaybackService.HasLiveOpticalPlayback(
                Snapshot(PlaybackState.Playing, @"C:\Movies\Blade Runner 2049.mkv"), "Blu-ray disc"),
            "Local file playback must not mark a Blu-ray title live");
        Check(!MpvPlaybackService.HasLiveOpticalPlayback(
                Snapshot(PlaybackState.Playing, @"C:\Movies\Blade Runner 2049.mkv"), "DVD disc"),
            "Local file playback must not mark a DVD title live");
    }

    private static void NullSourceIsNotLive()
    {
        Check(!MpvPlaybackService.HasLiveOpticalPlayback(
                Snapshot(PlaybackState.Playing, null), "Blu-ray disc"),
            "A null source must not be live");
    }

    private static void PrefixMatching()
    {
        Check(MpvPlaybackService.HasLiveOpticalPlayback(
                Snapshot(PlaybackState.Playing, "blu-ray disc • e:\\"), "Blu-ray disc"),
            "The source prefix match must be case-insensitive");
        Check(!MpvPlaybackService.HasLiveOpticalPlayback(
                Snapshot(PlaybackState.Playing, "DVD disc • D:\\"), "Blu-ray disc"),
            "A DVD source must not satisfy the Blu-ray prefix");
        Check(!MpvPlaybackService.HasLiveOpticalPlayback(
                Snapshot(PlaybackState.Playing, "Blu-ray disc • E:\\"), "DVD disc"),
            "A Blu-ray source must not satisfy the DVD prefix");
    }
}
