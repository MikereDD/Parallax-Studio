using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using ParallaxStudio.Core.Enums;
using ParallaxStudio.Core.Models;
using ParallaxStudio.Infrastructure.Playback;

namespace ParallaxStudio.DvdHostStub;

/// <summary>
/// Behaviour is selected with PARALLAX_DVD_STUB_MODE (inherited from the test process):
///   normal            answer every command; exit 0 after "shutdown"
///   hang-after-hello  answer "hello", then never answer again (a wedged native call)
///   slow-teardown     after "shutdown", close the pipe and keep running for
///                     PARALLAX_DVD_STUB_DELAY_MS (libmpv teardown after the pipe closes)
///   ignore-shutdown   acknowledge "shutdown" but never exit
///   die-after         exit with code 3 PARALLAX_DVD_STUB_DELAY_MS after connecting
///   slow-connect      wait PARALLAX_DVD_STUB_DELAY_MS before opening the pipe
/// PARALLAX_DVD_STUB_DURATION / PARALLAX_DVD_STUB_CHAPTERS set the loaded title's runtime
/// and chapter count; "STUB_TITLE {pid} {--title}" records the requested DVD selector.
///   stall-snapshot    answer normally for PARALLAX_DVD_STUB_DELAY_MS after connecting, then
///                     print "STUB_STALL {pid}" and never answer the next "snapshot"
/// The first stdout line is "STUB_PID {pid} MODE {mode}" so tests can track processes.
/// Requests are served by DvdHostRequestLoop, the same loop the real host uses.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = ParseArguments(args);
        var mode = Environment.GetEnvironmentVariable("PARALLAX_DVD_STUB_MODE") ?? "normal";
        var delay = int.TryParse(Environment.GetEnvironmentVariable("PARALLAX_DVD_STUB_DELAY_MS"), out var ms) ? ms : 1500;
        Console.WriteLine($"STUB_PID {Environment.ProcessId} MODE {mode}");
        Console.Out.Flush();

        // Mirror the real host: never outlive the parent application.
        if (int.TryParse(options.GetValueOrDefault("parent-pid"), out var parentPid))
            _ = WatchParentAsync(parentPid);

        if (mode == "slow-connect")
            await Task.Delay(delay);

        using var pipe = new NamedPipeServerStream(
            options["pipe"], PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await pipe.WaitForConnectionAsync();
        using var reader = new StreamReader(pipe);
        using var writer = new StreamWriter(pipe) { AutoFlush = true };

        if (mode == "die-after")
            _ = Task.Delay(delay).ContinueWith(_ => Environment.Exit(3));

        var connectedAt = Stopwatch.StartNew();
        // The loaded program's runtime and chapter count, as libmpv would report them.
        var duration = TimeSpan.TryParse(Environment.GetEnvironmentVariable("PARALLAX_DVD_STUB_DURATION"), CultureInfo.InvariantCulture, out var configured)
            ? configured
            : TimeSpan.FromMinutes(44);
        var chapterCount = int.TryParse(Environment.GetEnvironmentVariable("PARALLAX_DVD_STUB_CHAPTERS"), out var chapters) ? chapters : 0;
        Console.WriteLine($"STUB_TITLE {Environment.ProcessId} {options.GetValueOrDefault("title")}");
        var snapshot = new PlaybackSnapshot(
            PlaybackState.Playing, null, "Stub DVD", TimeSpan.FromSeconds(1), duration, 68, false);

        async Task<DvdHostResponse> HandleAsync(DvdHostRequest request, CancellationToken cancellationToken)
        {
            var command = request.Command;
            Console.WriteLine($"STUB_COMMAND {Environment.ProcessId} {command}");

            if (command != "hello" && mode == "hang-after-hello")
                await Task.Delay(Timeout.Infinite, cancellationToken);

            if (command == "snapshot" && mode == "stall-snapshot" && connectedAt.ElapsedMilliseconds >= delay)
            {
                Console.WriteLine($"STUB_STALL {Environment.ProcessId}");
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            switch (command)
            {
                case "hello":
                case "snapshot":
                    return new DvdHostResponse(true, Snapshot: snapshot);
                case "tracks":
                    return new DvdHostResponse(true, Tracks: []);
                case "chapters":
                    return new DvdHostResponse(true, Chapters: Enumerable.Range(0, chapterCount)
                        .Select(index => new MediaChapter(index, $"Chapter {index + 1}", TimeSpan.FromMinutes(5 * index)))
                        .ToArray());
                case "editions":
                    return new DvdHostResponse(true, Editions: []);
                case "toggle-pause":
                    snapshot = snapshot with { State = snapshot.State == PlaybackState.Paused ? PlaybackState.Playing : PlaybackState.Paused };
                    return new DvdHostResponse(true);
                default:
                    // Includes "shutdown": like the real host, only acknowledge here.
                    return new DvdHostResponse(true);
            }
        }

        while (true)
        {
            var acknowledgedShutdown = await DvdHostRequestLoop.RunAsync(reader, writer, HandleAsync, CancellationToken.None);
            Console.WriteLine($"STUB_LOOP_END {Environment.ProcessId} shutdown={acknowledgedShutdown}");
            if (!acknowledgedShutdown || mode != "ignore-shutdown") break;
        }

        if (mode == "slow-teardown")
        {
            pipe.Dispose();
            await Task.Delay(delay);
        }
        else if (mode == "ignore-shutdown")
        {
            await Task.Delay(Timeout.Infinite);
        }

        return 0;
    }

    private static async Task WatchParentAsync(int parentPid)
    {
        while (true)
        {
            await Task.Delay(250);
            try
            {
                using var parent = Process.GetProcessById(parentPid);
                if (parent.HasExited) Environment.Exit(4);
            }
            catch (ArgumentException)
            {
                Environment.Exit(4);
            }
        }
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index + 1 < args.Length; index += 2)
            if (args[index].StartsWith("--", StringComparison.Ordinal))
                result[args[index][2..]] = args[index + 1];
        return result;
    }
}
