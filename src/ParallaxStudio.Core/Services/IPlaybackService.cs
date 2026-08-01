using ParallaxStudio.Core.Events;
using ParallaxStudio.Core.Models;

namespace ParallaxStudio.Core.Services;

public interface IPlaybackService : IAsyncDisposable
{
    event EventHandler<PlaybackStateChangedEventArgs>? StateChanged;

    PlaybackSnapshot Snapshot { get; }
    bool IsInitialized { get; }
    string BackendDescription { get; }
    string? LastPlaybackError { get; }
    bool IsMakeMkvLibMmbdAvailable();
    string DiscDecryptionProvider { get; }

    Task<PlaybackBackendStatus> ProbeAsync(CancellationToken cancellationToken = default);
    Task InitializeAsync(nint windowHandle, CancellationToken cancellationToken = default);
    Task OpenFileAsync(string path, CancellationToken cancellationToken = default);
    Task OpenBluRayAsync(DiscSource disc, CancellationToken cancellationToken = default);
    Task OpenBluRayTitleAsync(DiscSource disc, int titleIndex, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BluRayTitle>> GetBluRayTitlesAsync(CancellationToken cancellationToken = default);
    Task TogglePauseAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task SeekRelativeAsync(double seconds, CancellationToken cancellationToken = default);
    Task SeekAbsoluteAsync(double seconds, CancellationToken cancellationToken = default);
    Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default);
    Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MediaTrack>> GetTracksAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MediaChapter>> GetChaptersAsync(CancellationToken cancellationToken = default);
    Task SelectTrackAsync(MediaTrack track, CancellationToken cancellationToken = default);
    Task SelectChapterAsync(int chapterIndex, CancellationToken cancellationToken = default);
    Task LoadExternalSubtitleAsync(string path, CancellationToken cancellationToken = default);
    Task SetSubtitleDelayAsync(double seconds, CancellationToken cancellationToken = default);
}
