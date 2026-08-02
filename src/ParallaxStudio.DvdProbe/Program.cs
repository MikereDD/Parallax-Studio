using System.Diagnostics;
using System.Globalization;
using ParallaxStudio.Infrastructure.Playback;

namespace ParallaxStudio.DvdProbe;

internal static class Program
{
    private const int ManagedFailure = 10;
    private const int PlaybackFailure = 20;
    private const int TimeoutFailure = 21;

    public static int Main(string[] args)
    {
        if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            Console.Error.WriteLine("A DVD device path is required.");
            return ManagedFailure;
        }

        var device = args[0];
        var titleNumber = args.Length >= 2 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
        var source = titleNumber > 0 ? $"dvd://{titleNumber}" : "dvd://";

        using var library = new LibMpvLibrary();
        nint context = nint.Zero;
        try
        {
            if (!library.TryLoad(out var loadError))
            {
                Console.Error.WriteLine(loadError ?? "libmpv could not be loaded.");
                return ManagedFailure;
            }

            context = library.Create();
            library.SetOption(context, "terminal", "no");
            library.SetOption(context, "osc", "no");
            library.SetOption(context, "idle", "yes");
            library.SetOption(context, "keep-open", "no");
            library.SetOption(context, "force-window", "no");
            library.SetOption(context, "vo", "null");
            library.SetOption(context, "ao", "null");
            library.SetOption(context, "pause", "yes");
            library.Initialize(context);
            library.RequestLogMessages(context, "info");
            library.SetProperty(context, "dvd-device", device);

            Console.WriteLine($"PROBE source={source} device={device}");
            library.Command(context, "loadfile", source, "replace");

            var timeout = Stopwatch.StartNew();
            while (timeout.Elapsed < TimeSpan.FromSeconds(45))
            {
                Thread.Sleep(100);
                foreach (var item in library.DrainEvents(context))
                {
                    Console.WriteLine($"{item.Level}:{item.Kind}:{item.Prefix}:{item.Message}");
                    if (item.Kind == "file-loaded")
                    {
                        Console.WriteLine("PROBE_OK");
                        return 0;
                    }

                    if (item.Kind == "end-file" && item.Level == "error")
                    {
                        Console.Error.WriteLine("PROBE_PLAYBACK_FAILED");
                        return PlaybackFailure;
                    }
                }
            }

            Console.Error.WriteLine("PROBE_TIMEOUT");
            return TimeoutFailure;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return ManagedFailure;
        }
        finally
        {
            if (context != nint.Zero)
            {
                try { library.TerminateDestroy(context); } catch { }
            }
        }
    }
}
