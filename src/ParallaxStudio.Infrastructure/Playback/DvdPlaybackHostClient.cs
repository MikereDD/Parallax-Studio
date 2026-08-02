using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;
using ParallaxStudio.Core.Enums;
using ParallaxStudio.Core.Models;
using ParallaxStudio.Core.Services;

namespace ParallaxStudio.Infrastructure.Playback;

internal sealed class DvdPlaybackHostClient(IAppLogger logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private Process? _process;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private bool _disposed;

    public bool IsActive =>
        !_disposed &&
        _process is { HasExited: false } &&
        _pipe is { IsConnected: true };

    public bool WasStarted { get; private set; }
    public int? ExitCode => _process is { HasExited: true } ? _process.ExitCode : null;

    public async Task StartAsync(
        nint videoWindowHandle,
        DiscSource disc,
        int? titleNumber,
        double volume,
        bool muted,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (videoWindowHandle == nint.Zero)
            throw new InvalidOperationException("The DVD playback host requires a valid video surface handle.");

        await StopHostAsync(CancellationToken.None).ConfigureAwait(false);

        var hostPath = Path.Combine(AppContext.BaseDirectory, "dvd-host", "ParallaxStudio.DvdPlaybackHost.exe");
        if (!File.Exists(hostPath))
            throw new InvalidOperationException($"The isolated DVD playback host is missing: {hostPath}");

        var device = Path.GetPathRoot(disc.DriveRoot) ?? disc.DriveRoot;
        var pipeName = $"ParallaxStudio-Dvd-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var startInfo = new ProcessStartInfo
        {
            FileName = hostPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(hostPath)!
        };
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--parent-hwnd");
        startInfo.ArgumentList.Add(videoWindowHandle.ToInt64().ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--device");
        startInfo.ArgumentList.Add(device);
        startInfo.ArgumentList.Add("--title");
        startInfo.ArgumentList.Add((titleNumber ?? 0).ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--volume");
        startInfo.ArgumentList.Add(Math.Clamp(volume, 0, 100).ToString("0.##", CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--muted");
        startInfo.ArgumentList.Add(muted ? "1" : "0");

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!_process.Start())
            throw new InvalidOperationException("The isolated DVD playback host could not be started.");

        WasStarted = true;
        _stdoutTask = RelayOutputAsync(_process.StandardOutput, isError: false);
        _stderrTask = RelayOutputAsync(_process.StandardError, isError: true);

        _pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(20));
            await _pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
            _reader = new StreamReader(_pipe);
            _writer = new StreamWriter(_pipe) { AutoFlush = true };

            var hello = await RequestAsync(new DvdHostRequest("hello"), cancellationToken).ConfigureAwait(false);
            if (!hello.Ok)
                throw new InvalidOperationException(hello.Error ?? "The DVD playback host did not initialize.");
        }
        catch
        {
            await StopHostAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public Task<PlaybackSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        RequestValueAsync("snapshot", response => response.Snapshot, cancellationToken);

    public async Task<IReadOnlyList<MediaTrack>> GetTracksAsync(CancellationToken cancellationToken) =>
        await RequestValueAsync<MediaTrack[]>(
            "tracks",
            response => response.Tracks,
            cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<MediaChapter>> GetChaptersAsync(CancellationToken cancellationToken) =>
        await RequestValueAsync<MediaChapter[]>(
            "chapters",
            response => response.Chapters,
            cancellationToken).ConfigureAwait(false);

    public Task TogglePauseAsync(CancellationToken cancellationToken) =>
        RequestOkAsync(new DvdHostRequest("toggle-pause"), cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        RequestOkAsync(new DvdHostRequest("stop"), cancellationToken);

    public Task SeekRelativeAsync(double seconds, CancellationToken cancellationToken) =>
        RequestOkAsync(new DvdHostRequest("seek-relative", Number: seconds), cancellationToken);

    public Task SeekAbsoluteAsync(double seconds, CancellationToken cancellationToken) =>
        RequestOkAsync(new DvdHostRequest("seek-absolute", Number: Math.Max(0, seconds)), cancellationToken);

    public Task SetMutedAsync(bool muted, CancellationToken cancellationToken) =>
        RequestOkAsync(new DvdHostRequest("set-muted", Number: muted ? 1 : 0), cancellationToken);

    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken) =>
        RequestOkAsync(new DvdHostRequest("set-volume", Number: Math.Clamp(volume, 0, 100)), cancellationToken);

    public Task SelectTrackAsync(MediaTrack track, CancellationToken cancellationToken) =>
        RequestOkAsync(new DvdHostRequest(
            "select-track",
            Id: track.Id,
            Kind: track.Kind.ToString()), cancellationToken);

    public Task SelectChapterAsync(int chapterIndex, CancellationToken cancellationToken) =>
        RequestOkAsync(new DvdHostRequest("select-chapter", Id: Math.Max(0, chapterIndex)), cancellationToken);

    public Task LoadExternalSubtitleAsync(string path, CancellationToken cancellationToken) =>
        RequestOkAsync(new DvdHostRequest("load-subtitle", Path: path), cancellationToken);

    public Task SetSubtitleDelayAsync(double seconds, CancellationToken cancellationToken) =>
        RequestOkAsync(new DvdHostRequest("subtitle-delay", Number: seconds), cancellationToken);

    public async Task StopHostAsync(CancellationToken cancellationToken)
    {
        if (_writer is not null && _pipe is { IsConnected: true })
        {
            try { await RequestOkAsync(new DvdHostRequest("shutdown"), cancellationToken).ConfigureAwait(false); }
            catch { }
        }

        if (_process is { HasExited: false })
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch
            {
                try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
            }
        }

        _reader?.Dispose();
        _writer?.Dispose();
        _pipe?.Dispose();
        _process?.Dispose();
        _reader = null;
        _writer = null;
        _pipe = null;
        _process = null;

        if (_stdoutTask is not null) { try { await _stdoutTask.ConfigureAwait(false); } catch { } }
        if (_stderrTask is not null) { try { await _stderrTask.ConfigureAwait(false); } catch { } }
        _stdoutTask = null;
        _stderrTask = null;
    }

    private async Task<T> RequestValueAsync<T>(
        string command,
        Func<DvdHostResponse, T?> selector,
        CancellationToken cancellationToken)
        where T : class
    {
        var response = await RequestAsync(new DvdHostRequest(command), cancellationToken).ConfigureAwait(false);
        if (!response.Ok)
            throw new InvalidOperationException(response.Error ?? $"DVD host command '{command}' failed.");
        return selector(response) ?? throw new InvalidOperationException($"DVD host command '{command}' returned no data.");
    }

    private async Task RequestOkAsync(DvdHostRequest request, CancellationToken cancellationToken)
    {
        var response = await RequestAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.Ok)
            throw new InvalidOperationException(response.Error ?? $"DVD host command '{request.Command}' failed.");
    }

    private async Task<DvdHostResponse> RequestAsync(DvdHostRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_writer is null || _reader is null || _pipe is not { IsConnected: true })
            {
                var exit = ExitCode;
                throw new InvalidOperationException(exit is int code
                    ? $"The isolated DVD playback host exited with code 0x{unchecked((uint)code):X8}."
                    : "The isolated DVD playback host is not connected.");
            }

            var json = JsonSerializer.Serialize(request);
            await _writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                var exit = ExitCode;
                throw new InvalidOperationException(exit is int code
                    ? $"The isolated DVD playback host exited with code 0x{unchecked((uint)code):X8}."
                    : "The isolated DVD playback host disconnected unexpectedly.");
            }

            return JsonSerializer.Deserialize<DvdHostResponse>(line)
                ?? throw new InvalidOperationException("The isolated DVD playback host returned an invalid response.");
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private async Task RelayOutputAsync(StreamReader reader, bool isError)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (isError) await logger.WarningAsync($"DVD host: {line}").ConfigureAwait(false);
                else await logger.InfoAsync($"DVD host: {line}").ConfigureAwait(false);
            }
        }
        catch
        {
            // Process termination closes the redirected streams.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await StopHostAsync(CancellationToken.None).ConfigureAwait(false);
        _disposed = true;
        _ioGate.Dispose();
    }
}
