using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ParallaxStudio.Core.Models;
using ParallaxStudio.Core.Services;

namespace ParallaxStudio.Infrastructure.Playback;

/// <summary>
/// Bounds for every interaction with the isolated DVD playback host. Each IPC request
/// (including the wait for the per-session I/O gate) and each teardown phase completes
/// or fails within its budget, so a wedged native host can never block the caller.
/// </summary>
internal sealed record DvdHostTimeouts(
    TimeSpan Connect,
    TimeSpan Request,
    TimeSpan Shutdown,
    TimeSpan ExitWait,
    TimeSpan KillWait,
    TimeSpan OutputDrain)
{
    public static DvdHostTimeouts Default { get; } = new(
        Connect: TimeSpan.FromSeconds(20),
        Request: TimeSpan.FromSeconds(10),
        Shutdown: TimeSpan.FromSeconds(3),
        ExitWait: TimeSpan.FromSeconds(3),
        KillWait: TimeSpan.FromSeconds(5),
        OutputDrain: TimeSpan.FromSeconds(2));

    /// <summary>
    /// Upper bound for one lifecycle operation (stop of the previous session followed by
    /// a new start). Waiting for the lifecycle gate is bounded by this budget.
    /// </summary>
    public TimeSpan LifecycleBudget =>
        Connect + Request + (Shutdown + ExitWait + KillWait + OutputDrain) * 2 + TimeSpan.FromSeconds(5);
}

internal enum DvdHostFailureKind
{
    NotConnected,
    Timeout,
    Disconnected,
    Exited,
    Protocol
}

internal enum DvdHostPhase
{
    None,
    Starting,
    Connected,
    Broken,
    Exited
}

internal sealed record DvdHostStatus(long SessionId, DvdHostPhase Phase, int? ExitCode, string? Reason);

/// <summary>
/// A failure of the transport to one specific host session. <see cref="SessionId"/> lets
/// the owner ignore failures from a session that has already been superseded.
/// </summary>
internal sealed class DvdHostSessionException(
    long sessionId,
    DvdHostFailureKind kind,
    string message,
    Exception? innerException = null) : InvalidOperationException(message, innerException)
{
    public long SessionId { get; } = sessionId;
    public DvdHostFailureKind Kind { get; } = kind;
}

internal sealed class DvdPlaybackHostClient : IAsyncDisposable
{
    private readonly IAppLogger _logger;
    private readonly string _hostPath;
    private readonly DvdHostTimeouts _timeouts;

    // Serializes StartAsync/StopHostAsync/DisposeAsync. Every holder is bounded by
    // DvdHostTimeouts, and waiters are bounded by LifecycleBudget. It is intentionally
    // never disposed so that a late caller observes ObjectDisposedException from
    // ThrowIfDisposed rather than from the semaphore.
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private HostSession? _session;
    private long _lastSessionId;
    private int _transitioning;
    private volatile bool _disposed;

    public DvdPlaybackHostClient(IAppLogger logger)
        : this(
            logger,
            Path.Combine(AppContext.BaseDirectory, "dvd-host", "ParallaxStudio.DvdPlaybackHost.exe"),
            DvdHostTimeouts.Default)
    {
    }

    internal DvdPlaybackHostClient(IAppLogger logger, string hostPath, DvdHostTimeouts timeouts)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hostPath = string.IsNullOrWhiteSpace(hostPath) ? throw new ArgumentException("A host path is required.", nameof(hostPath)) : hostPath;
        _timeouts = timeouts ?? throw new ArgumentNullException(nameof(timeouts));
    }

    /// <summary>Identifier of the current host session, or 0 when no session exists.</summary>
    public long SessionId => CurrentSession?.Id ?? 0;

    /// <summary>True while a start, stop, or dispose is replacing or tearing down a session.</summary>
    public bool IsTransitioning => Volatile.Read(ref _transitioning) > 0;

    public bool IsActive =>
        !_disposed &&
        CurrentSession is { Connected: true, Broken: false, HasExited: false };

    public bool WasStarted { get; private set; }

    public int? ExitCode => CurrentSession?.ExitCode;

    private HostSession? CurrentSession => Volatile.Read(ref _session);

    public DvdHostStatus GetStatus()
    {
        var session = CurrentSession;
        if (session is null)
            return new DvdHostStatus(0, IsTransitioning ? DvdHostPhase.Starting : DvdHostPhase.None, null, null);

        var phase = session.HasExited
            ? DvdHostPhase.Exited
            : session.Broken
                ? DvdHostPhase.Broken
                : session.Connected
                    ? DvdHostPhase.Connected
                    : DvdHostPhase.Starting;
        return new DvdHostStatus(session.Id, phase, session.ExitCode, session.BrokenReason);
    }

    public async Task StartAsync(
        nint videoWindowHandle,
        DiscSource disc,
        int? titleNumber,
        double volume,
        bool muted,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (videoWindowHandle == nint.Zero)
            throw new InvalidOperationException("The DVD playback host requires a valid video surface handle.");

        await EnterLifecycleAsync("start", cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            // Teardown of the previous session always runs to completion; cancellation only
            // skips its graceful shutdown request. A replacement is never launched while the
            // previous host could still be rendering into the shared surface.
            var previous = CurrentSession;
            if (previous is not null)
            {
                var previousFailures = await StopSessionCoreAsync(
                        previous,
                        "replaced by a new DVD host session",
                        graceful: !cancellationToken.IsCancellationRequested)
                    .ConfigureAwait(false);

                // If the previous host could not be confirmed stopped, starting another one
                // would leave two native players on the same surface; refuse instead.
                ThrowCleanupFailures(previous, previousFailures);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(_hostPath))
                throw new InvalidOperationException($"The isolated DVD playback host is missing: {_hostPath}");

            var session = LaunchSession(videoWindowHandle, disc, titleNumber, volume, muted);
            try
            {
                await ConnectSessionAsync(session, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await _logger.WarningAsync(
                    $"DVD host session {session.Id} (pid {session.ProcessId}) did not start: {ex.GetType().Name}: {ex.Message} Terminating it.",
                    CancellationToken.None).ConfigureAwait(false);

                // Never leave a half-started replacement behind, whatever caused the failure.
                var cleanupFailures = await StopSessionCoreAsync(session, "start failed", graceful: false).ConfigureAwait(false);
                if (cleanupFailures.Count > 0)
                    throw new AggregateException(
                        $"DVD host session {session.Id} failed to start and could not be cleaned up completely.",
                        cleanupFailures.Prepend(ex));
                throw;
            }

            await _logger.InfoAsync(
                $"DVD host session {session.Id} connected (pid {session.ProcessId}).",
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            ExitLifecycle();
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

    public async Task<IReadOnlyList<DvdEditionInfo>> GetEditionsAsync(CancellationToken cancellationToken) =>
        await RequestValueAsync<DvdEditionInfo[]>(
            "editions",
            response => response.Editions,
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

    /// <summary>
    /// Stops the current host session. Teardown always completes: a cancelled token only
    /// skips the graceful shutdown request, and an unresponsive host is terminated.
    /// Expected transport failures are logged; unexpected cleanup failures are logged and
    /// then rethrown as an <see cref="AggregateException"/> after cleanup has finished.
    /// </summary>
    public async Task StopHostAsync(CancellationToken cancellationToken)
    {
        if (_disposed) return;
        await EnterLifecycleAsync("stop", CancellationToken.None).ConfigureAwait(false);
        try
        {
            var session = CurrentSession;
            if (session is null) return;
            var failures = await StopSessionCoreAsync(session, "stop requested", graceful: !cancellationToken.IsCancellationRequested)
                .ConfigureAwait(false);
            ThrowCleanupFailures(session, failures);
        }
        finally
        {
            ExitLifecycle();
        }
    }

    /// <summary>
    /// Stops <paramref name="sessionId"/> only if it is still the current session. Used to
    /// reap a faulted session without touching a replacement that started meanwhile.
    /// Returns false when the session had already been replaced or stopped.
    /// </summary>
    public async Task<bool> StopSessionAsync(long sessionId, string reason)
    {
        if (_disposed) return false;
        await EnterLifecycleAsync("stop session", CancellationToken.None).ConfigureAwait(false);
        try
        {
            var session = CurrentSession;
            if (session is null || session.Id != sessionId) return false;
            var failures = await StopSessionCoreAsync(session, reason, graceful: false).ConfigureAwait(false);
            ThrowCleanupFailures(session, failures);
            return true;
        }
        finally
        {
            ExitLifecycle();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await EnterLifecycleAsync("dispose", CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            var session = CurrentSession;
            if (session is null) return;
            var failures = await StopSessionCoreAsync(session, "client disposed", graceful: true).ConfigureAwait(false);
            ThrowCleanupFailures(session, failures);
        }
        finally
        {
            ExitLifecycle();
        }
    }

    private HostSession LaunchSession(nint videoWindowHandle, DiscSource disc, int? titleNumber, double volume, bool muted)
    {
        var device = Path.GetPathRoot(disc.DriveRoot) ?? disc.DriveRoot;
        var pipeName = $"ParallaxStudio-Dvd-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var startInfo = new ProcessStartInfo
        {
            FileName = _hostPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(_hostPath)!
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
        startInfo.ArgumentList.Add((titleNumber ?? -1).ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--volume");
        startInfo.ArgumentList.Add(Math.Clamp(volume, 0, 100).ToString("0.##", CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--muted");
        startInfo.ArgumentList.Add(muted ? "1" : "0");

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var session = new HostSession(Interlocked.Increment(ref _lastSessionId), process, pipe);
        process.Exited += (_, _) => session.ObserveExit();

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("The isolated DVD playback host could not be started.");
        }
        catch
        {
            pipe.Dispose();
            process.Dispose();
            throw;
        }

        session.ProcessId = process.Id;
        session.StdoutTask = RelayOutputAsync(session, process.StandardOutput, isError: false);
        session.StderrTask = RelayOutputAsync(session, process.StandardError, isError: true);
        Volatile.Write(ref _session, session);
        WasStarted = true;
        return session;
    }

    private async Task ConnectSessionAsync(HostSession session, CancellationToken cancellationToken)
    {
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connectTimeout.CancelAfter(_timeouts.Connect);
            try
            {
                await session.Pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new DvdHostSessionException(
                    session.Id,
                    session.HasExited ? DvdHostFailureKind.Exited : DvdHostFailureKind.Timeout,
                    session.HasExited
                        ? $"The isolated DVD playback host exited with code {FormatExitCode(session.ExitCode)} before opening its control pipe."
                        : $"The isolated DVD playback host did not open its control pipe within {_timeouts.Connect.TotalSeconds:0.#} s.");
            }
        }

        // The pipe is owned (and disposed) by the session. The reader and writer leave it
        // open: disposing a reader first would close the pipe under the writer, whose
        // flush-on-dispose then throws (audit finding F1). AutoFlush leaves no buffered data.
        session.Reader = new StreamReader(session.Pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: -1, leaveOpen: true);
        session.Writer = new StreamWriter(session.Pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: -1, leaveOpen: true)
        {
            AutoFlush = true
        };
        session.Connected = true;

        var hello = await RequestAsync(session, new DvdHostRequest("hello"), _timeouts.Request, cancellationToken).ConfigureAwait(false);
        if (!hello.Ok)
            throw new InvalidOperationException(hello.Error ?? "The DVD playback host did not initialize.");
    }

    private async Task<T> RequestValueAsync<T>(
        string command,
        Func<DvdHostResponse, T?> selector,
        CancellationToken cancellationToken)
        where T : class
    {
        var response = await RequestCurrentAsync(new DvdHostRequest(command), cancellationToken).ConfigureAwait(false);
        if (!response.Ok)
            throw new InvalidOperationException(response.Error ?? $"DVD host command '{command}' failed.");
        return selector(response) ?? throw new InvalidOperationException($"DVD host command '{command}' returned no data.");
    }

    private async Task RequestOkAsync(DvdHostRequest request, CancellationToken cancellationToken)
    {
        var response = await RequestCurrentAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.Ok)
            throw new InvalidOperationException(response.Error ?? $"DVD host command '{request.Command}' failed.");
    }

    private Task<DvdHostResponse> RequestCurrentAsync(DvdHostRequest request, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var session = CurrentSession
            ?? throw new DvdHostSessionException(0, DvdHostFailureKind.NotConnected, "The isolated DVD playback host is not connected.");
        return RequestAsync(session, request, _timeouts.Request, cancellationToken);
    }

    /// <summary>
    /// Sends one request to <paramref name="session"/>. The whole operation, including the
    /// wait for the session's I/O gate, is bounded by <paramref name="timeout"/>. Any
    /// interruption after the request was written leaves the response stream in an
    /// unknown position, so the session is marked broken and never reused.
    /// </summary>
    private async Task<DvdHostResponse> RequestAsync(
        HostSession session,
        DvdHostRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            await session.IoGate.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DvdHostSessionException(
                session.Id,
                DvdHostFailureKind.Timeout,
                $"DVD host session {session.Id} I/O gate was not available within {timeout.TotalSeconds:0.#} s for '{request.Command}'; a previous request is still outstanding.");
        }

        try
        {
            ThrowIfUnusable(session, request.Command);
            var json = JsonSerializer.Serialize(request);
            try
            {
                await session.Writer!.WriteLineAsync(json.AsMemory(), deadline.Token).ConfigureAwait(false);
                var line = await session.Reader!.ReadLineAsync(deadline.Token).ConfigureAwait(false);
                if (line is null)
                {
                    session.MarkBroken($"control pipe closed during '{request.Command}'");
                    throw TransportFailure(session, request.Command, "closed the control pipe", null);
                }

                try
                {
                    return JsonSerializer.Deserialize<DvdHostResponse>(line)
                        ?? throw new JsonException("The response deserialized to null.");
                }
                catch (JsonException ex)
                {
                    session.MarkBroken($"invalid response to '{request.Command}'");
                    throw new DvdHostSessionException(
                        session.Id,
                        DvdHostFailureKind.Protocol,
                        $"The isolated DVD playback host returned an invalid response to '{request.Command}'.",
                        ex);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                session.MarkBroken($"'{request.Command}' timed out");
                throw new DvdHostSessionException(
                    session.Id,
                    DvdHostFailureKind.Timeout,
                    $"The isolated DVD playback host did not answer '{request.Command}' within {timeout.TotalSeconds:0.#} s.");
            }
            catch (OperationCanceledException)
            {
                session.MarkBroken($"'{request.Command}' was cancelled after it was sent");
                throw;
            }
            catch (IOException ex)
            {
                session.MarkBroken($"I/O failure during '{request.Command}': {ex.Message}");
                throw TransportFailure(session, request.Command, "control pipe failed", ex);
            }
            catch (ObjectDisposedException ex)
            {
                session.MarkBroken($"control pipe disposed during '{request.Command}'");
                throw TransportFailure(session, request.Command, "control pipe was closed", ex);
            }
        }
        finally
        {
            session.IoGate.Release();
        }
    }

    private static void ThrowIfUnusable(HostSession session, string command)
    {
        if (session.HasExited)
            throw new DvdHostSessionException(
                session.Id,
                DvdHostFailureKind.Exited,
                $"The isolated DVD playback host exited with code {FormatExitCode(session.ExitCode)}.");
        if (session.Broken)
            throw new DvdHostSessionException(
                session.Id,
                DvdHostFailureKind.Disconnected,
                $"DVD host session {session.Id} is no longer usable for '{command}' ({session.BrokenReason}).");
        if (!session.Connected || session.Writer is null || session.Reader is null)
            throw new DvdHostSessionException(
                session.Id,
                DvdHostFailureKind.NotConnected,
                "The isolated DVD playback host is not connected.");
    }

    private static DvdHostSessionException TransportFailure(HostSession session, string command, string what, Exception? inner) =>
        session.HasExited
            ? new DvdHostSessionException(
                session.Id,
                DvdHostFailureKind.Exited,
                $"The isolated DVD playback host exited with code {FormatExitCode(session.ExitCode)} during '{command}'.",
                inner)
            : new DvdHostSessionException(
                session.Id,
                DvdHostFailureKind.Disconnected,
                $"The isolated DVD playback host {what} during '{command}'.",
                inner);

    /// <summary>
    /// Terminates <paramref name="session"/>: optional bounded graceful shutdown, bounded
    /// exit wait, process-tree kill, bounded post-kill wait, then resource release.
    /// Returns unexpected failures (already logged) for the caller to surface.
    /// Must be called while holding the lifecycle gate.
    /// </summary>
    private async Task<List<Exception>> StopSessionCoreAsync(HostSession session, string reason, bool graceful)
    {
        var failures = new List<Exception>();

        // Detach first: pollers immediately stop treating this session as current, and any
        // in-flight failure on it is attributable to a superseded session id.
        if (ReferenceEquals(CurrentSession, session))
            Volatile.Write(ref _session, null);

        await _logger.InfoAsync(
            $"Stopping DVD host session {session.Id} (pid {session.ProcessId}): {reason}.",
            CancellationToken.None).ConfigureAwait(false);

        if (graceful && session.Connected && !session.Broken && !session.HasExited)
        {
            try
            {
                var response = await RequestAsync(session, new DvdHostRequest("shutdown"), _timeouts.Shutdown, CancellationToken.None)
                    .ConfigureAwait(false);
                if (!response.Ok)
                    await _logger.WarningAsync(
                        $"DVD host session {session.Id} rejected shutdown: {response.Error ?? "no detail"}.",
                        CancellationToken.None).ConfigureAwait(false);
            }
            catch (DvdHostSessionException ex)
            {
                await _logger.WarningAsync(
                    $"DVD host session {session.Id} graceful shutdown failed ({ex.Kind}): {ex.Message}",
                    CancellationToken.None).ConfigureAwait(false);
            }
        }

        if (!await WaitForExitAsync(session, _timeouts.ExitWait).ConfigureAwait(false))
        {
            await _logger.WarningAsync(
                $"DVD host session {session.Id} (pid {session.ProcessId}) did not exit within {_timeouts.ExitWait.TotalSeconds:0.#} s; terminating its process tree.",
                CancellationToken.None).ConfigureAwait(false);
            try
            {
                session.Process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (session.RefreshExited())
            {
                // The process exited between the wait and the kill; nothing is left to terminate.
            }
            catch (Exception ex) when (ex is Win32Exception or NotSupportedException or InvalidOperationException)
            {
                failures.Add(ex);
                await _logger.ErrorAsync(
                    $"Could not terminate DVD host session {session.Id} (pid {session.ProcessId}).",
                    ex,
                    CancellationToken.None).ConfigureAwait(false);
            }

            if (!await WaitForExitAsync(session, _timeouts.KillWait).ConfigureAwait(false))
            {
                var orphan = new TimeoutException(
                    $"DVD host session {session.Id} (pid {session.ProcessId}) was still running {_timeouts.KillWait.TotalSeconds:0.#} s after termination was requested.");
                failures.Add(orphan);
                await _logger.ErrorAsync(orphan.Message, orphan, CancellationToken.None).ConfigureAwait(false);
            }
        }

        if (session.HasExited)
            await _logger.InfoAsync(
                $"DVD host session {session.Id} exited with code {FormatExitCode(session.ExitCode)}.",
                CancellationToken.None).ConfigureAwait(false);

        try
        {
            session.Pipe.Dispose();
        }
        catch (IOException ex)
        {
            // A broken pipe can report the failure again on close; the session is already gone.
            await _logger.InfoAsync(
                $"DVD host session {session.Id} control pipe reported an error while closing: {ex.Message}",
                CancellationToken.None).ConfigureAwait(false);
        }

        if (await DrainOutputAsync(session).ConfigureAwait(false) is { } relayFailure)
            failures.Add(relayFailure);

        if (session.HasExited)
        {
            session.Process.Dispose();
        }
        else
        {
            // Keep the Process object alive: disposing it would discard the only handle
            // to a process that could not be confirmed terminated (logged above).
            await _logger.WarningAsync(
                $"DVD host session {session.Id} process handle retained because termination was not confirmed.",
                CancellationToken.None).ConfigureAwait(false);
        }

        return failures;
    }

    private static async Task<bool> WaitForExitAsync(HostSession session, TimeSpan timeout)
    {
        if (session.RefreshExited()) return true;
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            await session.Process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return session.RefreshExited();
        }

        session.ObserveExit();
        return true;
    }

    private async Task<Exception?> DrainOutputAsync(HostSession session)
    {
        var relays = new[] { session.StdoutTask, session.StderrTask }.OfType<Task>().ToArray();
        if (relays.Length == 0) return null;

        var all = Task.WhenAll(relays);
        var finished = await Task.WhenAny(all, Task.Delay(_timeouts.OutputDrain)).ConfigureAwait(false);
        if (!ReferenceEquals(finished, all))
        {
            await _logger.WarningAsync(
                $"DVD host session {session.Id} output did not close within {_timeouts.OutputDrain.TotalSeconds:0.#} s; a child process may still hold it.",
                CancellationToken.None).ConfigureAwait(false);
            return null;
        }

        if (!all.IsFaulted) return null;

        // RelayOutputAsync already handles the expected stream-closure failures; anything
        // else is unexpected and is reported to the caller after cleanup completes.
        var failure = all.Exception!.GetBaseException();
        await _logger.ErrorAsync(
            $"DVD host session {session.Id} output relay failed unexpectedly.",
            failure,
            CancellationToken.None).ConfigureAwait(false);
        return failure;
    }

    private async Task RelayOutputAsync(HostSession session, StreamReader reader, bool isError)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (isError) await _logger.WarningAsync($"DVD host: {line}").ConfigureAwait(false);
                else await _logger.InfoAsync($"DVD host: {line}").ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Termination closes the redirected streams; record it rather than discarding it.
            try
            {
                await _logger.InfoAsync(
                    $"DVD host session {session.Id} {(isError ? "stderr" : "stdout")} relay ended: {ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The application logger has already been disposed during process exit.
            }
        }
    }

    private async Task EnterLifecycleAsync(string operation, CancellationToken cancellationToken)
    {
        if (!await _lifecycleGate.WaitAsync(_timeouts.LifecycleBudget, cancellationToken).ConfigureAwait(false))
            throw new TimeoutException(
                $"DVD host {operation} could not begin within {_timeouts.LifecycleBudget.TotalSeconds:0} s because another host lifecycle operation did not finish.");
        Interlocked.Increment(ref _transitioning);
    }

    private void ExitLifecycle()
    {
        Interlocked.Decrement(ref _transitioning);
        _lifecycleGate.Release();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void ThrowCleanupFailures(HostSession session, List<Exception> failures)
    {
        if (failures.Count > 0)
            throw new AggregateException($"DVD host session {session.Id} could not be stopped cleanly.", failures);
    }

    private static string FormatExitCode(int? code) =>
        code is int value ? $"0x{unchecked((uint)value):X8}" : "unknown";

    private sealed class HostSession(long id, Process process, NamedPipeClientStream pipe)
    {
        private int _exited;
        private int _exitCode;
        private volatile bool _connected;
        private volatile bool _broken;
        private volatile string? _brokenReason;

        public long Id { get; } = id;
        public Process Process { get; } = process;
        public NamedPipeClientStream Pipe { get; } = pipe;

        // Per-session gate: a request stuck on a superseded session can never delay the
        // replacement. SemaphoreSlim holds no unmanaged resources unless its wait handle is
        // used, so it is not disposed (a late Release must not throw).
        public SemaphoreSlim IoGate { get; } = new(1, 1);
        public StreamReader? Reader { get; set; }
        public StreamWriter? Writer { get; set; }
        public Task? StdoutTask { get; set; }
        public Task? StderrTask { get; set; }
        public int ProcessId { get; set; }

        public bool Connected { get => _connected; set => _connected = value; }
        public bool Broken => _broken;
        public string? BrokenReason => _brokenReason;
        public bool HasExited => Volatile.Read(ref _exited) == 1;
        public int? ExitCode => HasExited ? Volatile.Read(ref _exitCode) : null;

        public void MarkBroken(string reason)
        {
            _brokenReason ??= reason;
            _broken = true;
        }

        /// <summary>Records the exit code once the process has exited. Safe from any thread.</summary>
        public void ObserveExit()
        {
            if (HasExited) return;
            try
            {
                Volatile.Write(ref _exitCode, Process.ExitCode);
            }
            catch (InvalidOperationException)
            {
                // Exited raced with disposal of a confirmed-exited handle; the code is unknown.
                Volatile.Write(ref _exitCode, -1);
            }
            Volatile.Write(ref _exited, 1);
        }

        /// <summary>Polls the process for exit; only called by the lifecycle owner.</summary>
        public bool RefreshExited()
        {
            if (HasExited) return true;
            if (!Process.HasExited) return false;
            ObserveExit();
            return true;
        }
    }
}
