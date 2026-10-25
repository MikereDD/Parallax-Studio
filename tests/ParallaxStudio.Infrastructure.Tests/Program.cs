using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using ParallaxStudio.Core.Enums;
using ParallaxStudio.Core.Models;
using ParallaxStudio.Core.Services;
using ParallaxStudio.Infrastructure.Playback;

namespace ParallaxStudio.Infrastructure.Tests;

/// <summary>
/// DVD playback-host lifecycle checks (audit findings F1–F3). They launch a stub host
/// that speaks the real IPC protocol, so no libmpv, libdvdcss, or optical drive is needed.
/// Real libdvdnav timing, HWND rendering, and disc behaviour still require TESTING.md.
/// </summary>
internal static partial class Program
{
    private static readonly DvdHostTimeouts TestTimeouts = new(
        Connect: TimeSpan.FromSeconds(3),
        Request: TimeSpan.FromSeconds(1),
        Shutdown: TimeSpan.FromSeconds(1),
        ExitWait: TimeSpan.FromSeconds(1),
        KillWait: TimeSpan.FromSeconds(3),
        OutputDrain: TimeSpan.FromSeconds(1));

    // Longest a full teardown of an unresponsive host may take with TestTimeouts, plus slack.
    private static readonly TimeSpan TeardownBound = TimeSpan.FromSeconds(9);

    private static readonly DiscSource Disc = new(
        Path.GetPathRoot(Path.GetTempPath()) ?? Path.GetTempPath(),
        "STUB_DVD",
        DiscKind.Dvd,
        true);

    private static async Task<int> Main(string[] args)
    {
        (string Name, Func<Task> Run)[] checks =
        [
            ("protocol: shutdown is acknowledged before the request loop returns", ShutdownAcknowledgedBeforeLoopReturnsAsync),
            ("protocol: a failing request gets an error response and the loop continues", FailingRequestKeepsLoopAsync),
            ("client: graceful shutdown is acknowledged and the host exits normally", GracefulShutdownAcknowledgedAsync),
            ("client: repeated start/stop never throws and leaves no process", RepeatedStartStopAsync),
            ("client: hung request times out and breaks only that session", HungRequestTimesOutAsync),
            ("client: waiting for the I/O gate is bounded", IoGateWaitIsBoundedAsync),
            ("client: stop with a hung host is bounded and kills the host", StopHungHostIsBoundedAsync),
            ("client: dispose with a hung host is bounded and kills the host", DisposeHungHostIsBoundedAsync),
            ("client: host that ignores shutdown is terminated", IgnoredShutdownIsTerminatedAsync),
            ("client: slow teardown completes without errors", SlowTeardownAsync),
            ("client: mid-session process death is detected and reaped", MidSessionDeathAsync),
            ("client: cancellation during start leaves no process", CancelDuringStartAsync),
            ("client: connect timeout leaves no process", ConnectTimeoutAsync),
            ("client: rapid concurrent replacement leaves exactly one host", ClientRapidReplacementAsync),
            ("client: session-scoped stop never touches a replacement", SessionScopedStopAsync),
            ("service: title switch with slow teardown keeps control of the new host", ServiceTitleSwitchSlowTeardownAsync),
            ("service: in-flight failure of a superseded host cannot fault its replacement", ServiceInFlightStaleFailureAsync),
            ("service: mid-session death faults playback and reaps the host", ServiceMidSessionDeathAsync),
            ("service: hung host faults playback and is killed", ServiceHungHostAsync),
            ("service: rapid concurrent title switches leave one controlled host", ServiceRapidReplacementAsync),
            ("service: failure before routing is claimed keeps current playback", ServiceEarlyFailureKeepsPlaybackAsync),
            ("service: cancelled replacement leaves no host running", ServiceCancelledReplacementAsync),
            ("service: dispose with a hung host is bounded", ServiceDisposeHungHostAsync),
            .. TitleMatchingChecks
        ];

        var filter = args.FirstOrDefault();
        var failed = 0;
        var ran = 0;
        foreach (var check in checks)
        {
            if (filter is not null && !check.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            ran++;
            var watch = Stopwatch.StartNew();
            try
            {
                await check.Run();
                Console.WriteLine($"PASS: {check.Name} ({watch.Elapsed.TotalSeconds:0.0} s)");
            }
            catch (Exception exception)
            {
                failed++;
                Console.Error.WriteLine($"FAIL: {check.Name} ({watch.Elapsed.TotalSeconds:0.0} s): {exception}");
            }
            finally
            {
                KillLeftoverStubs();
            }
        }

        Console.WriteLine($"Infrastructure checks: {ran - failed} passed, {failed} failed.");
        return failed == 0 ? 0 : 1;
    }

    // ----- DvdHostRequestLoop (server side, shared by the real host and the stub) -------

    private static async Task ShutdownAcknowledgedBeforeLoopReturnsAsync()
    {
        var input = new StringReader(string.Join('\n',
            Json(new DvdHostRequest("hello")),
            Json(new DvdHostRequest("shutdown")),
            Json(new DvdHostRequest("snapshot"))) + "\n");
        var output = new StringWriter();
        var handled = new List<string>();
        using var stop = new CancellationTokenSource();

        var acknowledged = await DvdHostRequestLoop.RunAsync(input, output, (request, _) =>
        {
            handled.Add(request.Command);
            return Task.FromResult(new DvdHostResponse(true));
        }, stop.Token);

        var responses = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Check(acknowledged, "The loop must report that it ended on an acknowledged shutdown");
        Check(handled.SequenceEqual(new[] { "hello", "shutdown" }), $"Requests after shutdown must not be served: {string.Join(",", handled)}");
        Check(responses.Length == 2, $"Expected 2 responses (hello + shutdown ack), got {responses.Length}");
        var ack = System.Text.Json.JsonSerializer.Deserialize<DvdHostResponse>(responses[1]);
        Check(ack is { Ok: true }, "The shutdown acknowledgment must be written");
    }

    private static async Task FailingRequestKeepsLoopAsync()
    {
        var input = new StringReader(Json(new DvdHostRequest("broken")) + "\nnot json\n" + Json(new DvdHostRequest("hello")) + "\n");
        var output = new StringWriter();
        var acknowledged = await DvdHostRequestLoop.RunAsync(input, output, (request, _) =>
            request.Command == "broken"
                ? throw new InvalidOperationException("boom")
                : Task.FromResult(new DvdHostResponse(true)), CancellationToken.None);

        var responses = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => System.Text.Json.JsonSerializer.Deserialize<DvdHostResponse>(line)!)
            .ToArray();
        Check(!acknowledged, "End of input is not a shutdown");
        Check(responses.Length == 3, $"Every request needs exactly one response, got {responses.Length}");
        Check(!responses[0].Ok && responses[0].Error == "boom", "Handler failures must be reported");
        Check(!responses[1].Ok, "Malformed requests must be reported");
        Check(responses[2].Ok, "The loop must keep serving after a failure");
    }

    private static string Json(DvdHostRequest request) => System.Text.Json.JsonSerializer.Serialize(request);

    // ----- DvdPlaybackHostClient -------------------------------------------------------

    private static async Task GracefulShutdownAcknowledgedAsync()
    {
        // Regression for the NETZACH log: "graceful shutdown failed (Disconnected): The
        // isolated DVD playback host closed the control pipe during 'shutdown'."
        var log = new TestLogger();
        await using var client = NewClient(log);
        UseStub("normal");
        await client.StartAsync(1, Disc, null, 68, false, CancellationToken.None);
        var pid = log.StubPids().Last();

        await client.StopHostAsync(CancellationToken.None);

        Check(!log.Contains("graceful shutdown failed"), "The shutdown request must be acknowledged");
        Check(!log.Contains("terminating its process tree"), "A host that acknowledges shutdown must exit without being killed");
        Check(log.Contains("exited with code 0x00000000"), "The host must exit normally with code 0");
        Check(!IsAlive(pid), "The host process must be gone");
        await WaitUntilAsync(() => log.Contains($"STUB_LOOP_END {pid} shutdown=True"), TimeSpan.FromSeconds(2), "the host loop to end on the acknowledged shutdown");
        log.AssertNoErrors();
    }

    private static async Task RepeatedStartStopAsync()
    {
        var log = new TestLogger();
        var client = NewClient(log);
        long previousSession = 0;
        for (var iteration = 0; iteration < 5; iteration++)
        {
            UseStub("normal");
            await client.StartAsync(1, Disc, iteration, 68, false, CancellationToken.None);
            Check(client.IsActive, "Host must be active after start");
            Check(client.SessionId > previousSession, "Session ids must increase");
            previousSession = client.SessionId;
            var snapshot = await client.GetSnapshotAsync(CancellationToken.None);
            Check(snapshot.State == PlaybackState.Playing, "Stub snapshot must round-trip");
            await client.StopHostAsync(CancellationToken.None); // F1: used to throw ObjectDisposedException
            Check(!log.Contains("graceful shutdown failed"), "Each graceful shutdown must be acknowledged");
            Check(client.GetStatus().Phase == DvdHostPhase.None, "No session may remain after stop");
        }

        await AssertAllStubsExitedAsync(log);
        log.AssertNoErrors();
        await client.DisposeAsync();
    }

    private static async Task HungRequestTimesOutAsync()
    {
        var log = new TestLogger();
        await using var client = NewClient(log);
        UseStub("hang-after-hello");
        await client.StartAsync(1, Disc, null, 68, false, CancellationToken.None);
        var session = client.SessionId;

        var watch = Stopwatch.StartNew();
        var failure = await CaptureAsync<DvdHostSessionException>(() => client.GetSnapshotAsync(CancellationToken.None));
        Check(failure.Kind == DvdHostFailureKind.Timeout, $"Expected a timeout, got {failure.Kind}");
        Check(failure.SessionId == session, "The failure must name the session it occurred on");
        Check(watch.Elapsed < TestTimeouts.Request + TimeSpan.FromSeconds(1.5), $"Request took {watch.Elapsed}");

        var status = client.GetStatus();
        Check(status.Phase == DvdHostPhase.Broken, $"A timed-out session must be broken, was {status.Phase}");
        Check(!client.IsActive, "A broken session must not report active");

        watch.Restart();
        var next = await CaptureAsync<DvdHostSessionException>(() => client.TogglePauseAsync(CancellationToken.None));
        Check(next.Kind == DvdHostFailureKind.Disconnected, $"Expected fast rejection, got {next.Kind}");
        Check(watch.Elapsed < TimeSpan.FromSeconds(0.5), "A broken session must reject requests immediately");
    }

    private static async Task IoGateWaitIsBoundedAsync()
    {
        // The shutdown budget (0.5 s) is shorter than the outstanding request's (2 s), so
        // the shutdown request must give up on the I/O gate itself rather than wait.
        var log = new TestLogger();
        var timeouts = TestTimeouts with { Request = TimeSpan.FromSeconds(2), Shutdown = TimeSpan.FromSeconds(0.5) };
        await using var client = new DvdPlaybackHostClient(log, StubPath, timeouts);
        UseStub("hang-after-hello");
        await client.StartAsync(1, Disc, null, 68, false, CancellationToken.None);
        var pid = log.StubPids().Last();

        var outstanding = client.GetSnapshotAsync(CancellationToken.None).ContinueWith(_ => { });
        await Task.Delay(100);
        var watch = Stopwatch.StartNew();
        await client.StopHostAsync(CancellationToken.None);
        Check(log.Contains("I/O gate was not available within 0.5 s for 'shutdown'"), "The gate wait must time out and be logged");
        Check(watch.Elapsed < TimeSpan.FromSeconds(0.5 + 1 + 3 + 1 + 1), $"Stop took {watch.Elapsed}");
        Check(!IsAlive(pid), "The host must be terminated after the gate timeout");

        // A request queued behind a hung request is also bounded by its own deadline.
        UseStub("hang-after-hello");
        await client.StartAsync(1, Disc, null, 68, false, CancellationToken.None);
        var first = client.GetSnapshotAsync(CancellationToken.None).ContinueWith(_ => { });
        await Task.Delay(100);
        watch.Restart();
        var queued = await CaptureAsync<DvdHostSessionException>(() => client.GetTracksAsync(CancellationToken.None));
        Check(queued.Kind is DvdHostFailureKind.Timeout or DvdHostFailureKind.Disconnected, $"Unexpected kind {queued.Kind}");
        Check(watch.Elapsed < timeouts.Request + TimeSpan.FromSeconds(1), $"Queued request took {watch.Elapsed}");
        await Task.WhenAll(outstanding, first);
    }

    private static async Task StopHungHostIsBoundedAsync()
    {
        var log = new TestLogger();
        var client = NewClient(log);
        UseStub("hang-after-hello");
        await client.StartAsync(1, Disc, null, 68, false, CancellationToken.None);
        var pid = log.StubPids().Last();
        _ = client.GetSnapshotAsync(CancellationToken.None).ContinueWith(_ => { }); // holds the I/O gate

        var watch = Stopwatch.StartNew();
        var stop = client.StopHostAsync(CancellationToken.None);
        Check(await Task.WhenAny(stop, Task.Delay(TeardownBound)) == stop, "StopHostAsync must finish within its bound");
        await stop;
        Check(!IsAlive(pid), "The hung host must be terminated");
        Check(log.Contains("terminating its process tree"), "Forced termination must be logged");
        Console.WriteLine($"      hung-host stop took {watch.Elapsed.TotalSeconds:0.0} s");
        await client.DisposeAsync();
    }

    private static async Task DisposeHungHostIsBoundedAsync()
    {
        var log = new TestLogger();
        var client = NewClient(log);
        UseStub("hang-after-hello");
        await client.StartAsync(1, Disc, null, 68, false, CancellationToken.None);
        var pid = log.StubPids().Last();

        var dispose = client.DisposeAsync().AsTask();
        Check(await Task.WhenAny(dispose, Task.Delay(TeardownBound)) == dispose, "DisposeAsync must finish within its bound");
        await dispose;
        Check(!IsAlive(pid), "Dispose must terminate a hung host");
        await CaptureAsync<ObjectDisposedException>(() => client.StartAsync(1, Disc, null, 68, false, CancellationToken.None));
    }

    private static async Task IgnoredShutdownIsTerminatedAsync()
    {
        var log = new TestLogger();
        await using var client = NewClient(log);
        UseStub("ignore-shutdown");
        await client.StartAsync(1, Disc, null, 68, false, CancellationToken.None);
        var pid = log.StubPids().Last();
        var stop = client.StopHostAsync(CancellationToken.None);
        Check(await Task.WhenAny(stop, Task.Delay(TeardownBound)) == stop, "Stop must be bounded");
        await stop;
        Check(!IsAlive(pid), "A host that acknowledges but ignores shutdown must be terminated");
        Check(log.Contains("did not exit within"), "The exit timeout must be logged");
    }

    private static async Task SlowTeardownAsync()
    {
        var log = new TestLogger();
        await using var client = NewClient(log);
        UseStub("slow-teardown", delayMs: 1500);
        await client.StartAsync(1, Disc, null, 68, false, CancellationToken.None);
        var pid = log.StubPids().Last();
        var stop = client.StopHostAsync(CancellationToken.None);
        Check(await Task.WhenAny(stop, Task.Delay(TeardownBound)) == stop, "Stop must be bounded");
        await stop;
        Check(!IsAlive(pid), "Host must be gone after stop");
        log.AssertNoErrors();
    }

    private static async Task MidSessionDeathAsync()
    {
        var log = new TestLogger();
        await using var client = NewClient(log);
        UseStub("die-after", delayMs: 500);
        await client.StartAsync(1, Disc, null, 68, false, CancellationToken.None);
        var session = client.SessionId;

        await WaitUntilAsync(() => client.GetStatus().Phase == DvdHostPhase.Exited, TimeSpan.FromSeconds(4), "host exit to be observed");
        Check(client.ExitCode == 3, $"Exit code must be captured, was {client.ExitCode}");
        var failure = await CaptureAsync<DvdHostSessionException>(() => client.GetSnapshotAsync(CancellationToken.None));
        Check(failure.Kind == DvdHostFailureKind.Exited && failure.Message.Contains("0x00000003"), $"Unexpected failure: {failure.Kind} {failure.Message}");

        Check(await client.StopSessionAsync(session, "test reap"), "The exited session must be reaped");
        Check(client.GetStatus().Phase == DvdHostPhase.None, "No session may remain after reaping");
        log.AssertNoErrors();
    }

    private static async Task CancelDuringStartAsync()
    {
        var log = new TestLogger();
        await using var client = NewClient(log);
        UseStub("slow-connect", delayMs: 5000);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await CaptureAsync<OperationCanceledException>(() => client.StartAsync(1, Disc, null, 68, false, cancel.Token));
        Check(client.GetStatus().Phase == DvdHostPhase.None, "A cancelled start must not leave a session");
        await AssertAllStubsExitedAsync(log);
    }

    private static async Task ConnectTimeoutAsync()
    {
        var log = new TestLogger();
        await using var client = NewClient(log);
        UseStub("slow-connect", delayMs: 15000);
        var watch = Stopwatch.StartNew();
        var failure = await CaptureAsync<DvdHostSessionException>(() => client.StartAsync(1, Disc, null, 68, false, CancellationToken.None));
        Check(failure.Kind == DvdHostFailureKind.Timeout, $"Expected connect timeout, got {failure.Kind}");
        Check(watch.Elapsed < TestTimeouts.Connect + TeardownBound, $"Start took {watch.Elapsed}");
        await AssertAllStubsExitedAsync(log);
    }

    private static async Task ClientRapidReplacementAsync()
    {
        var log = new TestLogger();
        await using var client = NewClient(log);
        UseStub("slow-teardown", delayMs: 400);
        var starts = Enumerable.Range(0, 6)
            .Select(index => Task.Run(() => client.StartAsync(1, Disc, index, 68, false, CancellationToken.None)))
            .ToArray();
        await Task.WhenAll(starts);

        await WaitUntilAsync(() => log.StubPids().Count(IsAlive) == 1, TimeSpan.FromSeconds(5), "exactly one stub to remain");
        Check(log.StubPids().Count == 6, $"Expected 6 launches, saw {log.StubPids().Count}");
        Check(client.IsActive, "The last replacement must be active");
        await client.GetSnapshotAsync(CancellationToken.None);
        log.AssertNoErrors();
    }

    private static async Task SessionScopedStopAsync()
    {
        var log = new TestLogger();
        await using var client = NewClient(log);
        UseStub("normal");
        await client.StartAsync(1, Disc, null, 68, false, CancellationToken.None);
        var first = client.SessionId;
        await client.StartAsync(1, Disc, 1, 68, false, CancellationToken.None);
        var second = client.SessionId;

        Check(!await client.StopSessionAsync(first, "stale reap"), "Stopping a superseded session id must be a no-op");
        Check(client.IsActive && client.SessionId == second, "The replacement must be untouched");
        Check(await client.StopSessionAsync(second, "current reap"), "Stopping the current session must succeed");
    }

    // ----- MpvPlaybackService routing ---------------------------------------------------

    private static async Task ServiceTitleSwitchSlowTeardownAsync()
    {
        var log = new TestLogger();
        await using var service = NewService(log);
        UseStub("slow-teardown", delayMs: 1500);
        await service.OpenDvdAsync(Disc);
        await WaitUntilAsync(() => service.Snapshot.State == PlaybackState.Playing, TimeSpan.FromSeconds(3), "first host to play");

        for (var title = 1; title <= 3; title++)
        {
            await service.OpenDvdTitleAsync(Disc, title); // F1 + F3
            await Task.Delay(750); // several poll ticks while the previous host is still tearing down
            Check(service.Snapshot.State == PlaybackState.Playing, $"Title {title}: state must follow the new host, was {service.Snapshot.State}");
            var latest = log.StubPids().Last();
            await service.TogglePauseAsync(); // used to reach the uninitialized in-process libmpv path
            await WaitUntilAsync(() => log.Contains($"STUB_COMMAND {latest} toggle-pause"), TimeSpan.FromSeconds(2), "pause to reach the new host");
            await service.TogglePauseAsync();
        }

        Check(!log.Contains("stopped responding"), "A superseded host's teardown must not be reported as a failure");
        log.AssertNoErrors();
    }

    private static async Task ServiceInFlightStaleFailureAsync()
    {
        // The old host stops answering a poll request just as a title switch begins, so the
        // poll's timeout arrives after the new routing generation was claimed. Without the
        // generation/session ownership checks this failure disowned the new host (F3).
        var log = new TestLogger();
        await using var service = NewService(log);
        UseStub("stall-snapshot", delayMs: 600);
        await service.OpenDvdAsync(Disc);
        var stalled = log.StubPids().Last();
        await WaitUntilAsync(() => log.Contains($"STUB_STALL {stalled}"), TimeSpan.FromSeconds(4), "the old host to stall a poll request");

        UseStub("normal");
        await service.OpenDvdTitleAsync(Disc, 2);
        await Task.Delay(750);

        Check(!log.Errors().Any(entry => entry.Contains("stopped responding")), "The superseded host's failure must not be reported as a playback fault");
        Check(service.Snapshot.State == PlaybackState.Playing, $"State must follow the replacement, was {service.Snapshot.State}");
        var replacement = log.StubPids().Last();
        Check(replacement != stalled && !IsAlive(stalled), "The stalled host must be gone and replaced");
        await service.TogglePauseAsync();
        await WaitUntilAsync(() => log.Contains($"STUB_COMMAND {replacement} toggle-pause"), TimeSpan.FromSeconds(2), "pause to reach the replacement");
        Check(log.Contains("polling failure from superseded DVD host session"), "The race must have been exercised and attributed to the superseded session");
    }

    private static async Task ServiceMidSessionDeathAsync()
    {
        var log = new TestLogger();
        await using var service = NewService(log);
        UseStub("die-after", delayMs: 800);
        await service.OpenDvdAsync(Disc);
        var pid = log.StubPids().Last();

        await WaitUntilAsync(() => service.Snapshot.State == PlaybackState.Faulted, TimeSpan.FromSeconds(4), "playback to fault");
        Check(service.LastPlaybackError?.Contains("exited with code 0x00000003") == true, $"Unexpected error text: {service.LastPlaybackError}");
        Check(log.Errors().Any(entry => entry.Contains("exited with code 0x00000003")), "The exit must be logged as an error");
        Check(!IsAlive(pid), "The dead host must not be running");
        await WaitUntilAsync(() => log.Contains("faulted during playback polling"), TimeSpan.FromSeconds(4), "the faulted session to be reaped");
    }

    private static async Task ServiceHungHostAsync()
    {
        var log = new TestLogger();
        await using var service = NewService(log);
        UseStub("hang-after-hello");
        await service.OpenDvdAsync(Disc);
        var pid = log.StubPids().Last();

        await WaitUntilAsync(() => service.Snapshot.State == PlaybackState.Faulted, TimeSpan.FromSeconds(5), "a hung host to fault playback");
        Check(service.LastPlaybackError?.Contains("did not answer 'snapshot'") == true, $"Unexpected error text: {service.LastPlaybackError}");
        Check(log.Errors().Any(entry => entry.Contains("stopped responding")), "The hang must be logged as an error");
        await WaitUntilAsync(() => !IsAlive(pid), TeardownBound, "the hung host to be killed");
    }

    private static async Task ServiceRapidReplacementAsync()
    {
        var log = new TestLogger();
        await using var service = NewService(log);
        UseStub("slow-teardown", delayMs: 600);
        await service.OpenDvdAsync(Disc);
        await WaitUntilAsync(() => service.Snapshot.State == PlaybackState.Playing, TimeSpan.FromSeconds(3), "first host to play");

        var switches = Enumerable.Range(1, 4)
            .Select(title => Task.Run(() => service.OpenDvdTitleAsync(Disc, title)))
            .ToArray();
        await Task.WhenAll(switches);
        await Task.Delay(750);

        await WaitUntilAsync(() => log.StubPids().Count(IsAlive) == 1, TimeSpan.FromSeconds(5), "exactly one host to remain");
        Check(service.Snapshot.State == PlaybackState.Playing, $"State must follow the surviving host, was {service.Snapshot.State}");
        var survivor = log.StubPids().Single(IsAlive);
        await service.TogglePauseAsync();
        await WaitUntilAsync(() => log.Contains($"STUB_COMMAND {survivor} toggle-pause"), TimeSpan.FromSeconds(2), "pause to reach the surviving host");
        log.AssertNoErrors();
    }

    private static async Task ServiceEarlyFailureKeepsPlaybackAsync()
    {
        var log = new TestLogger();
        await using var service = NewService(log);
        UseStub("normal");
        await service.OpenDvdAsync(Disc);
        await WaitUntilAsync(() => service.Snapshot.State == PlaybackState.Playing, TimeSpan.FromSeconds(3), "host to play");
        var pid = log.StubPids().Last();

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await CaptureAsync<OperationCanceledException>(() => service.OpenDvdTitleAsync(Disc, 2, cancelled.Token));

        Check(IsAlive(pid), "A failure before the replacement started must not stop current playback");
        await service.TogglePauseAsync();
        await WaitUntilAsync(() => log.Contains($"STUB_COMMAND {pid} toggle-pause"), TimeSpan.FromSeconds(2), "existing host to stay controlled");
        log.AssertNoErrors();
    }

    private static async Task ServiceCancelledReplacementAsync()
    {
        var log = new TestLogger();
        await using var service = NewService(log);
        UseStub("normal");
        await service.OpenDvdAsync(Disc);
        await WaitUntilAsync(() => service.Snapshot.State == PlaybackState.Playing, TimeSpan.FromSeconds(3), "host to play");

        UseStub("slow-connect", delayMs: 5000);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await CaptureAsync<OperationCanceledException>(() => service.OpenDvdTitleAsync(Disc, 2, cancel.Token));

        await AssertAllStubsExitedAsync(log);
        await Task.Delay(500);
        Check(service.Snapshot.State != PlaybackState.Playing, "No host remains, so playback must not report Playing");
    }

    private static async Task ServiceDisposeHungHostAsync()
    {
        var log = new TestLogger();
        var service = NewService(log);
        UseStub("hang-after-hello");
        await service.OpenDvdAsync(Disc);
        var pid = log.StubPids().Last();

        var dispose = service.DisposeAsync().AsTask();
        Check(await Task.WhenAny(dispose, Task.Delay(TeardownBound)) == dispose, "Service dispose must be bounded");
        await dispose;
        Check(!IsAlive(pid), "Dispose must not leave the hung host running");
    }

    // ----- helpers ----------------------------------------------------------------------

    private static DvdPlaybackHostClient NewClient(TestLogger log) => new(log, StubPath, TestTimeouts);

    private static MpvPlaybackService NewService(TestLogger log)
    {
        var service = new MpvPlaybackService(log, logger => new DvdPlaybackHostClient(logger, StubPath, TestTimeouts));
        service.StartPollingForTesting(1);
        return service;
    }

    private static string StubPath { get; } = new[] { "ParallaxStudio.DvdHostStub.exe", "ParallaxStudio.DvdHostStub" }
        .Select(name => Path.Combine(AppContext.BaseDirectory, name))
        .FirstOrDefault(File.Exists)
        ?? throw new FileNotFoundException("The DVD host stub was not built beside the test runner.");

    private static void UseStub(string mode, int delayMs = 1500)
    {
        Environment.SetEnvironmentVariable("PARALLAX_DVD_STUB_MODE", mode);
        Environment.SetEnvironmentVariable("PARALLAX_DVD_STUB_DELAY_MS", delayMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("PARALLAX_DVD_STUB_DURATION", null);
        Environment.SetEnvironmentVariable("PARALLAX_DVD_STUB_CHAPTERS", null);
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task AssertAllStubsExitedAsync(TestLogger log)
    {
        await WaitUntilAsync(() => log.StubPids().Count > 0, TimeSpan.FromSeconds(3), "a stub launch to be recorded");
        await WaitUntilAsync(() => !log.StubPids().Any(IsAlive), TimeSpan.FromSeconds(3), "every stub process to exit");
    }

    private static void KillLeftoverStubs()
    {
        foreach (var process in Process.GetProcessesByName("ParallaxStudio.DvdHostStub"))
        {
            using (process)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > timeout) throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0.#} s waiting for {what}.");
            await Task.Delay(50);
        }
    }

    private static async Task<TException> CaptureAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException expected)
        {
            return expected;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}, but the operation succeeded.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed partial class TestLogger : IAppLogger
{
    private readonly ConcurrentQueue<(string Level, string Message)> _entries = new();

    public Task InfoAsync(string message, CancellationToken cancellationToken = default) => Add("INFO", message, null);
    public Task WarningAsync(string message, CancellationToken cancellationToken = default) => Add("WARN", message, null);
    public Task ErrorAsync(string message, Exception? exception = null, CancellationToken cancellationToken = default) => Add("ERROR", message, exception);

    public bool Contains(string text) => _entries.Any(entry => entry.Message.Contains(text, StringComparison.Ordinal));

    public IReadOnlyList<string> Errors() => _entries.Where(entry => entry.Level == "ERROR").Select(entry => entry.Message).ToArray();

    public IReadOnlyList<int> StubPids() => _entries
        .Select(entry => StubPidPattern().Match(entry.Message))
        .Where(match => match.Success)
        .Select(match => int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
        .Distinct()
        .ToArray();

    public void AssertNoErrors()
    {
        var errors = Errors();
        if (errors.Count > 0) throw new InvalidOperationException("Unexpected ERROR log entries:\n  " + string.Join("\n  ", errors));
    }

    private Task Add(string level, string message, Exception? exception)
    {
        _entries.Enqueue((level, exception is null ? message : $"{message} :: {exception.GetType().Name}: {exception.Message}"));
        if (Environment.GetEnvironmentVariable("PARALLAX_TEST_VERBOSE") == "1")
            Console.WriteLine($"      [{level}] {message}{(exception is null ? string.Empty : " :: " + exception.Message)}");
        return Task.CompletedTask;
    }

    // Client lifecycle logs name the pid synchronously ("(pid N)"); the stub's own
    // "STUB_PID N" line arrives asynchronously through the stdout relay.
    [GeneratedRegex(@"(?:DVD host: STUB_PID |\(pid )(\d+)")]
    private static partial Regex StubPidPattern();
}
