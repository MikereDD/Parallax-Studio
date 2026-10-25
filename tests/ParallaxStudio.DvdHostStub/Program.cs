using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using ParallaxStudio.Core.Enums;
using ParallaxStudio.Core.Models;

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
///   stall-snapshot    answer normally for PARALLAX_DVD_STUB_DELAY_MS after connecting, then
///                     print "STUB_STALL {pid}" and never answer the next "snapshot"
/// The first stdout line is "STUB_PID {pid} MODE {mode}" so tests can track processes.
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
        var snapshot = new PlaybackSnapshot(
            PlaybackState.Playing, null, "Stub DVD", TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(44), 68, false);

        while (await reader.ReadLineAsync() is { } line)
        {
            var command = JsonDocument.Parse(line).RootElement.GetProperty("Command").GetString();
            Console.WriteLine($"STUB_COMMAND {Environment.ProcessId} {command}");

            if (command != "hello" && mode == "hang-after-hello")
                await Task.Delay(Timeout.Infinite);

            if (command == "snapshot" && mode == "stall-snapshot" && connectedAt.ElapsedMilliseconds >= delay)
            {
                Console.WriteLine($"STUB_STALL {Environment.ProcessId}");
                await Task.Delay(Timeout.Infinite);
            }

            switch (command)
            {
                case "hello":
                case "snapshot":
                    await Send(writer, new { Ok = true, Snapshot = snapshot });
                    break;
                case "tracks":
                    await Send(writer, new { Ok = true, Tracks = Array.Empty<MediaTrack>() });
                    break;
                case "chapters":
                    await Send(writer, new { Ok = true, Chapters = Array.Empty<MediaChapter>() });
                    break;
                case "editions":
                    await Send(writer, new { Ok = true, Editions = Array.Empty<object>() });
                    break;
                case "toggle-pause":
                    snapshot = snapshot with { State = snapshot.State == PlaybackState.Paused ? PlaybackState.Playing : PlaybackState.Paused };
                    await Send(writer, new { Ok = true });
                    break;
                case "shutdown":
                    await Send(writer, new { Ok = true });
                    if (mode == "ignore-shutdown") break;
                    if (mode == "slow-teardown")
                    {
                        pipe.Dispose();
                        await Task.Delay(delay);
                    }
                    return 0;
                default:
                    await Send(writer, new { Ok = true });
                    break;
            }
        }

        if (mode == "ignore-shutdown")
            await Task.Delay(Timeout.Infinite);
        return 0;
    }

    private static Task Send(StreamWriter writer, object response) =>
        writer.WriteLineAsync(JsonSerializer.Serialize(response));

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
