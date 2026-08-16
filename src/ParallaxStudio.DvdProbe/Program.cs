using System.Diagnostics;
using System.Globalization;
using System.Text;
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
        var titleSelector = args.Length >= 2 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : -1;
        var source = titleSelector >= 0 ? $"dvd://{titleSelector}" : "dvd://";

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
                        WriteEditionMap(library, context);
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
    private static void WriteEditionMap(LibMpvLibrary library, nint context)
    {
        var count = ParseInt(library.GetProperty(context, "edition-list/count"));
        var current = ParseInt(library.GetProperty(context, "edition"), -1);
        for (var index = 0; index < count; index++)
        {
            var prefix = $"edition-list/{index}/";
            var selector = ParseInt(library.GetProperty(context, prefix + "id"), index);
            var title = library.GetProperty(context, prefix + "title") ?? string.Empty;
            var duration = ParseEditionDuration(title);
            var encodedTitle = Convert.ToBase64String(Encoding.UTF8.GetBytes(title));
            Console.WriteLine($"DVD_EDITION|{selector}|{duration.TotalSeconds.ToString("R", CultureInfo.InvariantCulture)}|{(selector == current ? 1 : 0)}|{encodedTitle}");
        }
    }

    private static TimeSpan ParseEditionDuration(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return TimeSpan.Zero;
        var open = title.LastIndexOf('(');
        var close = title.LastIndexOf(')');
        if (open < 0 || close <= open + 1) return TimeSpan.Zero;
        return TimeSpan.TryParse(title[(open + 1)..close], CultureInfo.InvariantCulture, out var duration)
            ? duration
            : TimeSpan.Zero;
    }

    private static int ParseInt(string? value, int fallback = 0) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : fallback;

}
