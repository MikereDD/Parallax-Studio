using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;
using ParallaxStudio.Core.Enums;
using ParallaxStudio.Core.Models;
using ParallaxStudio.Infrastructure.Playback;

namespace ParallaxStudio.DvdPlaybackHost;

internal static class Program
{
    private static readonly SemaphoreSlim MpvGate = new(1, 1);
    private static readonly CancellationTokenSource Shutdown = new();
    private static readonly LibMpvLibrary Library = new();
    private static nint _context;
    private static PlaybackSnapshot _snapshot = PlaybackSnapshot.Disconnected();
    private static bool _paused;
    private static string? _lastError;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = ParseArguments(args);
            var pipeName = Required(options, "pipe");
            var device = Required(options, "device");
            var hwnd = long.Parse(Required(options, "parent-hwnd"), CultureInfo.InvariantCulture);
            var parentPid = int.Parse(Required(options, "parent-pid"), CultureInfo.InvariantCulture);
            var titleNumber = ParseInt(options.GetValueOrDefault("title"));
            var volume = ParseDouble(options.GetValueOrDefault("volume"), 68);
            var muted = options.GetValueOrDefault("muted") == "1";
            var source = titleNumber > 0 ? $"dvd://{titleNumber}" : "dvd://";

            if (!Library.TryLoad(out var loadError))
                throw new InvalidOperationException(loadError ?? "libmpv could not be loaded.");

            _context = Library.Create();
            Library.SetOption(_context, "wid", hwnd.ToString(CultureInfo.InvariantCulture));
            Library.SetOption(_context, "terminal", "no");
            Library.SetOption(_context, "osc", "no");
            Library.SetOption(_context, "idle", "yes");
            Library.SetOption(_context, "keep-open", "yes");
            Library.SetOption(_context, "force-window", "no");
            Library.SetOption(_context, "input-default-bindings", "no");
            Library.SetOption(_context, "input-vo-keyboard", "no");
            Library.Initialize(_context);
            Library.RequestLogMessages(_context, "info");
            Library.SetProperty(_context, "volume", Math.Clamp(volume, 0, 100).ToString("0.##", CultureInfo.InvariantCulture));
            Library.SetProperty(_context, "mute", muted ? "yes" : "no");
            Library.SetProperty(_context, "dvd-device", device);

            _snapshot = new PlaybackSnapshot(
                PlaybackState.Loading,
                $"DVD disc • {device}{(titleNumber > 0 ? $" • title {titleNumber}" : string.Empty)}",
                titleNumber > 0 ? $"DVD — Title {titleNumber}" : "DVD — Longest title",
                TimeSpan.Zero,
                TimeSpan.Zero,
                Math.Clamp(volume, 0, 100),
                muted);

            using var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            await pipe.WaitForConnectionAsync(Shutdown.Token).ConfigureAwait(false);

            using var reader = new StreamReader(pipe);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };

            var pollTask = PollAsync(parentPid, Shutdown.Token);
            Library.Command(_context, "loadfile", source, "replace");
            Console.WriteLine($"START source={source} device={device} hwnd={hwnd}");

            while (!Shutdown.IsCancellationRequested && pipe.IsConnected)
            {
                var line = await reader.ReadLineAsync(Shutdown.Token).ConfigureAwait(false);
                if (line is null) break;

                DvdHostResponse response;
                try
                {
                    var request = JsonSerializer.Deserialize<DvdHostRequest>(line)
                        ?? throw new InvalidOperationException("Invalid DVD host request.");
                    response = await HandleRequestAsync(request, Shutdown.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    response = new DvdHostResponse(false, ex.Message);
                }

                await writer.WriteLineAsync(JsonSerializer.Serialize(response).AsMemory(), Shutdown.Token).ConfigureAwait(false);
            }

            Shutdown.Cancel();
            try { await pollTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 10;
        }
        finally
        {
            if (_context != nint.Zero)
            {
                try { Library.Command(_context, "stop"); } catch { }
                try { Library.TerminateDestroy(_context); } catch { }
                _context = nint.Zero;
            }
            Library.Dispose();
            MpvGate.Dispose();
            Shutdown.Dispose();
        }
    }

    private static async Task<DvdHostResponse> HandleRequestAsync(DvdHostRequest request, CancellationToken cancellationToken)
    {
        switch (request.Command)
        {
            case "hello":
                return new DvdHostResponse(true, Snapshot: _snapshot);
            case "snapshot":
                return new DvdHostResponse(true, Snapshot: _snapshot);
            case "tracks":
                return new DvdHostResponse(true, Tracks: (await ReadTracksAsync(cancellationToken).ConfigureAwait(false)).ToArray());
            case "chapters":
                return new DvdHostResponse(true, Chapters: (await ReadChaptersAsync(cancellationToken).ConfigureAwait(false)).ToArray());
            case "toggle-pause":
                await WithMpvAsync(() =>
                {
                    _paused = !_paused;
                    Library.SetProperty(_context, "pause", _paused ? "yes" : "no");
                    _snapshot = _snapshot with { State = _paused ? PlaybackState.Paused : PlaybackState.Playing };
                }, cancellationToken).ConfigureAwait(false);
                return new DvdHostResponse(true);
            case "stop":
                await WithMpvAsync(() => Library.Command(_context, "stop"), cancellationToken).ConfigureAwait(false);
                _snapshot = _snapshot with { State = PlaybackState.Stopped, Position = TimeSpan.Zero, Duration = TimeSpan.Zero };
                return new DvdHostResponse(true);
            case "seek-relative":
                await WithMpvAsync(() => Library.Command(_context, "seek", (request.Number ?? 0).ToString("0.###", CultureInfo.InvariantCulture), "relative+exact"), cancellationToken).ConfigureAwait(false);
                return new DvdHostResponse(true);
            case "seek-absolute":
                await WithMpvAsync(() => Library.Command(_context, "seek", Math.Max(0, request.Number ?? 0).ToString("0.###", CultureInfo.InvariantCulture), "absolute+exact"), cancellationToken).ConfigureAwait(false);
                return new DvdHostResponse(true);
            case "set-muted":
                var muted = (request.Number ?? 0) >= 0.5;
                await WithMpvAsync(() => Library.SetProperty(_context, "mute", muted ? "yes" : "no"), cancellationToken).ConfigureAwait(false);
                _snapshot = _snapshot with { IsMuted = muted };
                return new DvdHostResponse(true);
            case "set-volume":
                var volume = Math.Clamp(request.Number ?? 0, 0, 100);
                await WithMpvAsync(() => Library.SetProperty(_context, "volume", volume.ToString("0.##", CultureInfo.InvariantCulture)), cancellationToken).ConfigureAwait(false);
                _snapshot = _snapshot with { Volume = volume };
                return new DvdHostResponse(true);
            case "select-track":
                var property = Enum.TryParse<MediaTrackKind>(request.Kind, out var kind) ? kind switch
                {
                    MediaTrackKind.Audio => "aid",
                    MediaTrackKind.Subtitle => "sid",
                    MediaTrackKind.Video => "vid",
                    _ => throw new InvalidOperationException("Unsupported track kind.")
                } : throw new InvalidOperationException("Invalid track kind.");
                await WithMpvAsync(() => Library.SetProperty(_context, property, (request.Id ?? 0) <= 0 ? "no" : (request.Id ?? 0).ToString(CultureInfo.InvariantCulture)), cancellationToken).ConfigureAwait(false);
                return new DvdHostResponse(true);
            case "select-chapter":
                await WithMpvAsync(() => Library.SetProperty(_context, "chapter", Math.Max(0, request.Id ?? 0).ToString(CultureInfo.InvariantCulture)), cancellationToken).ConfigureAwait(false);
                return new DvdHostResponse(true);
            case "load-subtitle":
                if (string.IsNullOrWhiteSpace(request.Path) || !File.Exists(request.Path))
                    throw new FileNotFoundException("Subtitle file not found.", request.Path);
                await WithMpvAsync(() => Library.Command(_context, "sub-add", request.Path, "select"), cancellationToken).ConfigureAwait(false);
                return new DvdHostResponse(true);
            case "subtitle-delay":
                await WithMpvAsync(() => Library.SetProperty(_context, "sub-delay", (request.Number ?? 0).ToString("0.###", CultureInfo.InvariantCulture)), cancellationToken).ConfigureAwait(false);
                return new DvdHostResponse(true);
            case "shutdown":
                Shutdown.Cancel();
                return new DvdHostResponse(true);
            default:
                return new DvdHostResponse(false, $"Unknown DVD host command: {request.Command}");
        }
    }

    private static async Task PollAsync(int parentPid, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (!IsProcessAlive(parentPid))
                {
                    Shutdown.Cancel();
                    return;
                }

                await WithMpvAsync(() =>
                {
                    foreach (var item in Library.DrainEvents(_context))
                    {
                        if (item.Kind == "log")
                            Console.WriteLine($"{item.Level}:{item.Prefix}:{item.Message}");
                        else if (item.Kind == "file-loaded")
                        {
                            _paused = string.Equals(Library.GetProperty(_context, "pause"), "yes", StringComparison.OrdinalIgnoreCase);
                            _snapshot = _snapshot with { State = _paused ? PlaybackState.Paused : PlaybackState.Playing };
                            Console.WriteLine("FILE_LOADED");
                        }
                        else if (item.Kind == "end-file")
                        {
                            if (item.Level == "error")
                            {
                                _lastError = item.Message;
                                _snapshot = _snapshot with { State = PlaybackState.Faulted };
                                Console.Error.WriteLine($"PLAYBACK_ERROR {item.Message}");
                            }
                            else
                            {
                                _snapshot = _snapshot with { State = PlaybackState.Stopped };
                            }
                        }
                    }

                    if (_snapshot.State is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Loading)
                    {
                        var position = ParseSeconds(Library.GetProperty(_context, "time-pos"));
                        var duration = ParseSeconds(Library.GetProperty(_context, "duration"));
                        var paused = string.Equals(Library.GetProperty(_context, "pause"), "yes", StringComparison.OrdinalIgnoreCase);
                        _paused = paused;
                        _snapshot = _snapshot with
                        {
                            Position = position,
                            Duration = duration,
                            State = _snapshot.State == PlaybackState.Loading
                                ? PlaybackState.Loading
                                : paused ? PlaybackState.Paused : PlaybackState.Playing
                        };
                    }
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _snapshot = _snapshot with { State = PlaybackState.Faulted };
                Console.Error.WriteLine(ex);
            }
        }
    }

    private static async Task<IReadOnlyList<MediaTrack>> ReadTracksAsync(CancellationToken cancellationToken)
    {
        var result = new List<MediaTrack>();
        await WithMpvAsync(() =>
        {
            var count = ParseInt(Library.GetProperty(_context, "track-list/count"));
            var activeAudioId = ParseInt(Library.GetProperty(_context, "aid"), -1);
            var activeSubtitleId = ParseInt(Library.GetProperty(_context, "sid"), -1);
            var activeVideoId = ParseInt(Library.GetProperty(_context, "vid"), -1);
            for (var index = 0; index < count; index++)
            {
                var prefix = $"track-list/{index}/";
                var type = Library.GetProperty(_context, prefix + "type");
                var kind = type switch
                {
                    "video" => MediaTrackKind.Video,
                    "audio" => MediaTrackKind.Audio,
                    "sub" => MediaTrackKind.Subtitle,
                    _ => (MediaTrackKind?)null
                };
                if (kind is null) continue;

                var id = ParseInt(Library.GetProperty(_context, prefix + "id"));
                var title = Library.GetProperty(_context, prefix + "title");
                var language = Library.GetProperty(_context, prefix + "lang");
                var codec = Library.GetProperty(_context, prefix + "codec");
                var channels = Library.GetProperty(_context, prefix + "demux-channel-count");
                var channelLayout = Library.GetProperty(_context, prefix + "demux-channels");
                var details = kind == MediaTrackKind.Audio
                    ? FirstNonEmpty(channelLayout, !string.IsNullOrWhiteSpace(channels) ? $"{channels} ch" : null)
                    : null;
                var label = string.IsNullOrWhiteSpace(title) ? $"{kind} {id}" : title;

                result.Add(new MediaTrack(
                    id,
                    kind.Value,
                    label,
                    language,
                    codec,
                    details,
                    IsYes(Library.GetProperty(_context, prefix + "default")),
                    IsYes(Library.GetProperty(_context, prefix + "forced")),
                    kind.Value switch
                    {
                        MediaTrackKind.Audio => id == activeAudioId,
                        MediaTrackKind.Subtitle => id == activeSubtitleId,
                        MediaTrackKind.Video => id == activeVideoId,
                        _ => false
                    }));
            }
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async Task<IReadOnlyList<MediaChapter>> ReadChaptersAsync(CancellationToken cancellationToken)
    {
        var result = new List<MediaChapter>();
        await WithMpvAsync(() =>
        {
            var count = ParseInt(Library.GetProperty(_context, "chapter-list/count"));
            var current = ParseInt(Library.GetProperty(_context, "chapter"), -1);
            for (var index = 0; index < count; index++)
            {
                var prefix = $"chapter-list/{index}/";
                var title = Library.GetProperty(_context, prefix + "title");
                var start = ParseSeconds(Library.GetProperty(_context, prefix + "time"));
                result.Add(new MediaChapter(index, string.IsNullOrWhiteSpace(title) ? $"Chapter {index + 1}" : title, start, index == current));
            }
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async Task WithMpvAsync(Action action, CancellationToken cancellationToken)
    {
        await MpvGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { action(); }
        finally { MpvGate.Release(); }
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index + 1 < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal)) continue;
            result[args[index][2..]] = args[index + 1];
        }
        return result;
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing --{key}.");

    private static bool IsProcessAlive(int processId)
    {
        try { return !Process.GetProcessById(processId).HasExited; }
        catch { return false; }
    }

    private static int ParseInt(string? value, int fallback = 0) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : fallback;

    private static double ParseDouble(string? value, double fallback = 0) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : fallback;

    private static bool IsYes(string? value) =>
        string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) || value == "1" ||
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static TimeSpan ParseSeconds(string? value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            !double.IsFinite(seconds) || seconds < 0)
            return TimeSpan.Zero;
        return TimeSpan.FromSeconds(seconds);
    }
}
