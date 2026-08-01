using System.Globalization;
using ParallaxStudio.Core.Enums;
using ParallaxStudio.Core.Events;
using ParallaxStudio.Core.Models;
using ParallaxStudio.Core.Services;

namespace ParallaxStudio.Infrastructure.Playback;

public sealed class MpvPlaybackService(IAppLogger logger) : IPlaybackService
{
    private readonly LibMpvLibrary _library = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _pollCancellation = new();
    private nint _context;
    private Task? _pollTask;
    private bool _disposed;
    private bool _paused;

    public event EventHandler<PlaybackStateChangedEventArgs>? StateChanged;

    public PlaybackSnapshot Snapshot { get; private set; } = PlaybackSnapshot.Disconnected();
    public bool IsInitialized => _context != nint.Zero;
    public string BackendDescription { get; private set; } = "libmpv not checked";

    public async Task<PlaybackBackendStatus> ProbeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_library.TryLoad(out var error))
            {
                BackendDescription = "libmpv missing";
                return new PlaybackBackendStatus(false, BackendDescription, Error: error);
            }

            BackendDescription = _library.GetClientApiVersion();
            return new PlaybackBackendStatus(true, BackendDescription, _library.LoadedPath);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task InitializeAsync(nint windowHandle, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (windowHandle == nint.Zero) throw new ArgumentException("A valid video window handle is required.", nameof(windowHandle));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsInitialized) return;
            if (!_library.TryLoad(out var loadError))
            {
                throw new FileNotFoundException(loadError, "libmpv-2.dll");
            }

            _context = _library.Create();
            try
            {
                _library.SetOption(_context, "wid", windowHandle.ToInt64().ToString(CultureInfo.InvariantCulture));
                _library.SetOption(_context, "terminal", "no");
                _library.SetOption(_context, "osc", "no");
                _library.SetOption(_context, "idle", "yes");
                _library.SetOption(_context, "keep-open", "yes");
                _library.SetOption(_context, "force-window", "no");
                _library.SetOption(_context, "input-default-bindings", "no");
                _library.SetOption(_context, "input-vo-keyboard", "no");
                _library.Initialize(_context);
                _library.SetProperty(_context, "volume", Snapshot.Volume.ToString("0.##", CultureInfo.InvariantCulture));

                BackendDescription = _library.GetClientApiVersion();
                SetSnapshot(Snapshot with { State = PlaybackState.Idle });
                _pollTask = PollPlaybackAsync(_pollCancellation.Token);
                await logger.InfoAsync($"Initialized {BackendDescription} from {_library.LoadedPath}.", cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                _library.TerminateDestroy(_context);
                _context = nint.Zero;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task OpenFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A media path is required.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Media file not found.", fullPath);

        await WithInitializedContextAsync(context =>
        {
            SetSnapshot(Snapshot with
            {
                State = PlaybackState.Loading,
                Source = fullPath,
                Title = Path.GetFileNameWithoutExtension(fullPath),
                Position = TimeSpan.Zero,
                Duration = TimeSpan.Zero
            });

            _library.Command(context, "loadfile", fullPath, "replace");
            _paused = false;
        }, cancellationToken).ConfigureAwait(false);

        await logger.InfoAsync($"Opened media: {fullPath}", cancellationToken).ConfigureAwait(false);
    }

    public Task TogglePauseAsync(CancellationToken cancellationToken = default) =>
        WithInitializedContextAsync(context =>
        {
            _paused = !_paused;
            _library.SetProperty(context, "pause", _paused ? "yes" : "no");
            SetSnapshot(Snapshot with { State = _paused ? PlaybackState.Paused : PlaybackState.Playing });
        }, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        WithInitializedContextAsync(context =>
        {
            _library.Command(context, "stop");
            _paused = false;
            SetSnapshot(Snapshot with
            {
                State = PlaybackState.Stopped,
                Position = TimeSpan.Zero,
                Duration = TimeSpan.Zero
            });
        }, cancellationToken);

    public Task SeekRelativeAsync(double seconds, CancellationToken cancellationToken = default) =>
        WithInitializedContextAsync(context =>
            _library.Command(
                context,
                "seek",
                seconds.ToString("0.###", CultureInfo.InvariantCulture),
                "relative+exact"),
            cancellationToken);

    public Task SeekAbsoluteAsync(double seconds, CancellationToken cancellationToken = default) =>
        WithInitializedContextAsync(context =>
            _library.Command(
                context,
                "seek",
                Math.Max(0, seconds).ToString("0.###", CultureInfo.InvariantCulture),
                "absolute+exact"),
            cancellationToken);

    public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default) =>
        WithInitializedContextAsync(context =>
        {
            _library.SetProperty(context, "mute", muted ? "yes" : "no");
            SetSnapshot(Snapshot with { IsMuted = muted });
        }, cancellationToken);

    public async Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default)
    {
        volume = Math.Clamp(volume, 0, 100);
        SetSnapshot(Snapshot with { Volume = volume });
        if (!IsInitialized) return;

        await WithInitializedContextAsync(context =>
            _library.SetProperty(context, "volume", volume.ToString("0.##", CultureInfo.InvariantCulture)),
            cancellationToken).ConfigureAwait(false);
    }


    public async Task<IReadOnlyList<MediaTrack>> GetTracksAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<MediaTrack>();
        await WithInitializedContextAsync(context =>
        {
            var count = ParseInt(_library.GetProperty(context, "track-list/count"));
            for (var index = 0; index < count; index++)
            {
                var prefix = $"track-list/{index}/";
                var type = _library.GetProperty(context, prefix + "type");
                var kind = type switch
                {
                    "video" => MediaTrackKind.Video,
                    "audio" => MediaTrackKind.Audio,
                    "sub" => MediaTrackKind.Subtitle,
                    _ => (MediaTrackKind?)null
                };
                if (kind is null) continue;

                var id = ParseInt(_library.GetProperty(context, prefix + "id"));
                var title = _library.GetProperty(context, prefix + "title");
                var language = _library.GetProperty(context, prefix + "lang");
                var codec = _library.GetProperty(context, prefix + "codec");
                var channels = _library.GetProperty(context, prefix + "demux-channel-count");
                var channelLayout = _library.GetProperty(context, prefix + "demux-channels");
                var details = kind == MediaTrackKind.Audio
                    ? FirstNonEmpty(channelLayout, !string.IsNullOrWhiteSpace(channels) ? $"{channels} ch" : null)
                    : null;
                var label = string.IsNullOrWhiteSpace(title)
                    ? $"{kind} {id}"
                    : title;

                result.Add(new MediaTrack(
                    id, kind.Value, label, language, codec, details,
                    IsYes(_library.GetProperty(context, prefix + "default")),
                    IsYes(_library.GetProperty(context, prefix + "forced")),
                    IsYes(_library.GetProperty(context, prefix + "selected"))));
            }
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<MediaChapter>> GetChaptersAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<MediaChapter>();
        await WithInitializedContextAsync(context =>
        {
            var count = ParseInt(_library.GetProperty(context, "chapter-list/count"));
            var current = ParseInt(_library.GetProperty(context, "chapter"), -1);
            for (var index = 0; index < count; index++)
            {
                var prefix = $"chapter-list/{index}/";
                var title = _library.GetProperty(context, prefix + "title");
                var start = ParseSeconds(_library.GetProperty(context, prefix + "time"));
                result.Add(new MediaChapter(index, string.IsNullOrWhiteSpace(title) ? $"Chapter {index + 1}" : title, start, index == current));
            }
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public Task SelectTrackAsync(MediaTrack track, CancellationToken cancellationToken = default) =>
        WithInitializedContextAsync(context =>
        {
            var property = track.Kind switch
            {
                MediaTrackKind.Audio => "aid",
                MediaTrackKind.Subtitle => "sid",
                MediaTrackKind.Video => "vid",
                _ => throw new ArgumentOutOfRangeException(nameof(track))
            };
            _library.SetProperty(context, property, track.Id <= 0 ? "no" : track.Id.ToString(CultureInfo.InvariantCulture));
        }, cancellationToken);

    public Task SelectChapterAsync(int chapterIndex, CancellationToken cancellationToken = default) =>
        WithInitializedContextAsync(context =>
            _library.SetProperty(context, "chapter", Math.Max(0, chapterIndex).ToString(CultureInfo.InvariantCulture)),
            cancellationToken);

    public async Task LoadExternalSubtitleAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A subtitle path is required.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Subtitle file not found.", fullPath);
        await WithInitializedContextAsync(context => _library.Command(context, "sub-add", fullPath, "select"), cancellationToken)
            .ConfigureAwait(false);
    }

    public Task SetSubtitleDelayAsync(double seconds, CancellationToken cancellationToken = default) =>
        WithInitializedContextAsync(context =>
            _library.SetProperty(context, "sub-delay", seconds.ToString("0.###", CultureInfo.InvariantCulture)),
            cancellationToken);

    private async Task PollPlaybackAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (!IsInitialized) continue;

                    var position = ParseSeconds(_library.GetProperty(_context, "time-pos"));
                    var duration = ParseSeconds(_library.GetProperty(_context, "duration"));
                    var pause = _library.GetProperty(_context, "pause");
                    var idle = _library.GetProperty(_context, "idle-active");
                    var mute = _library.GetProperty(_context, "mute");

                    _paused = string.Equals(pause, "yes", StringComparison.OrdinalIgnoreCase);
                    var state = Snapshot.State;
                    if (!string.IsNullOrWhiteSpace(Snapshot.Source))
                    {
                        state = string.Equals(idle, "yes", StringComparison.OrdinalIgnoreCase)
                            ? PlaybackState.Stopped
                            : _paused ? PlaybackState.Paused : PlaybackState.Playing;
                    }

                    var next = Snapshot with
                    {
                        State = state,
                        Position = position,
                        Duration = duration,
                        IsMuted = string.Equals(mute, "yes", StringComparison.OrdinalIgnoreCase)
                    };

                    if (next != Snapshot) SetSnapshot(next);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    await logger.WarningAsync($"Playback property polling skipped: {ex.Message}", cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }


    private static int ParseInt(string? value, int fallback = 0) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : fallback;

    private static bool IsYes(string? value) =>
        string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) || value == "1" ||
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static TimeSpan ParseSeconds(string? value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            !double.IsFinite(seconds) || seconds < 0)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private async Task WithInitializedContextAsync(Action<nint> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsInitialized) throw new InvalidOperationException("libmpv is not initialized yet.");
            action(_context);
        }
        catch (Exception ex)
        {
            SetSnapshot(Snapshot with { State = PlaybackState.Faulted });
            await logger.ErrorAsync("libmpv operation failed.", ex, cancellationToken).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void SetSnapshot(PlaybackSnapshot snapshot)
    {
        Snapshot = snapshot;
        StateChanged?.Invoke(this, new PlaybackStateChangedEventArgs(snapshot));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _pollCancellation.Cancel();

        if (_pollTask is not null)
        {
            try { await _pollTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_context != nint.Zero)
            {
                // Stop audio/video immediately before destroying the client.
                // mpv_terminate_destroy is still the authoritative cleanup step.
                try { _library.Command(_context, "stop"); }
                catch { /* Destruction below must proceed even if stop fails. */ }

                _library.TerminateDestroy(_context);
                _context = nint.Zero;
            }
            _library.Dispose();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            _pollCancellation.Dispose();
        }
    }
}
