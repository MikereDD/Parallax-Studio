using System.IO;
using System.Windows;
using ParallaxStudio.App;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ParallaxStudio.App.Services;
using ParallaxStudio.Core.Enums;
using ParallaxStudio.Core.Models;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var name in new[] { "DarkMonochrome", "Typography", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/ParallaxStudio.App;component/Themes/{name}.xaml", UriKind.Absolute) });
        application.Startup += async (_, _) =>
        {
            try { application.Shutdown(await RunAsync(args)); }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                application.Shutdown(1);
            }
        };
        return application.Run();
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var tests = new TestRun();
        var artwork = new LocalArtworkService();

        await tests.RunAsync("Missing, remote, mpv, and relative sources have no artwork", async () =>
        {
            foreach (var source in new string?[] { null, "", "movie.mkv", "https://example.com/movie.mkv", "bluray://1/E:/", "dvd://1/E:/" })
                TestRun.Check(await artwork.LoadAsync(source) is null, $"Unexpected artwork for {source}");
            using var fixture = new ArtworkFixture();
            TestRun.Check(await artwork.LoadAsync(fixture.Media) is null, "Missing artwork should return null");
        });

        await tests.RunAsync("Specific media artwork takes priority over folder art", async () =>
        {
            using var fixture = new ArtworkFixture();
            fixture.WriteImage("poster.jpg", 80, 160);
            fixture.WriteImage("movie.png", 1000, 500);
            var image = (BitmapSource?)await artwork.LoadAsync(fixture.Media);
            TestRun.Check(image?.PixelWidth == 480 && image.PixelHeight == 240, "Expected bounded media-specific image");
        });

        await tests.RunAsync("Corrupt images fall through to the next folder candidate", async () =>
        {
            using var fixture = new ArtworkFixture();
            File.WriteAllText(fixture.PathFor("movie.jpg"), "corrupt image");
            File.WriteAllText(fixture.PathFor("poster.jpg"), "corrupt image");
            fixture.WriteImage("folder.jpeg", 60, 90);
            fixture.WriteImage("cover.jpg", 40, 40);
            var image = (BitmapSource?)await artwork.LoadAsync(fixture.Media);
            TestRun.Check(image?.PixelWidth == 60 && image.PixelHeight == 90, "Expected folder.jpeg after corrupt poster");
        });

        await tests.RunAsync("Cover art is used when specific, poster, and folder artwork are absent", async () =>
        {
            using var fixture = new ArtworkFixture();
            fixture.WriteImage("cover.jpg", 40, 60);
            var image = (BitmapSource?)await artwork.LoadAsync(fixture.Media);
            TestRun.Check(image?.PixelWidth == 40 && image.PixelHeight == 60, "Expected cover.jpg");
        });

        await tests.RunAsync("Locked artwork falls through to another local image", async () =>
        {
            using var fixture = new ArtworkFixture();
            fixture.WriteImage("movie.jpg", 100, 150);
            fixture.WriteImage("poster.png", 80, 120);
            using var locked = File.Open(fixture.PathFor("movie.jpg"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var image = (BitmapSource?)await artwork.LoadAsync(fixture.Media);
            TestRun.Check(image?.PixelHeight == 120, "Expected fallback from locked media artwork");
        });

        await tests.RunAsync("Portrait decode stays bounded and preserves aspect ratio", async () =>
        {
            using var fixture = new ArtworkFixture();
            fixture.WriteImage("movie.jpg", 500, 1000);
            var image = (BitmapSource?)await artwork.LoadAsync(fixture.Media);
            TestRun.Check(image?.PixelWidth == 240 && image.PixelHeight == 480, "Expected 240x480 portrait");
        });

        await tests.RunAsync("Returned artwork is frozen and remains usable after its file is removed", async () =>
        {
            using var fixture = new ArtworkFixture();
            fixture.WriteImage("movie.png", 100, 150);
            var image = (BitmapSource?)await artwork.LoadAsync(fixture.Media);
            TestRun.Check(image?.IsFrozen == true, "Artwork must be frozen for safe UI transfer");
            // Exclusive access also catches readers that retain a shared file handle.
            using (File.Open(fixture.PathFor("movie.png"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            File.Delete(fixture.PathFor("movie.png"));
            image!.CopyPixels(new byte[image.PixelWidth * image.PixelHeight * 4], image.PixelWidth * 4, 0);
        });

        await tests.RunAsync("File URIs and directory sources can use local artwork", async () =>
        {
            using var fixture = new ArtworkFixture();
            fixture.WriteImage("poster.png", 80, 120);
            TestRun.Check(await artwork.LoadAsync(new Uri(fixture.Media).AbsoluteUri) is not null, "File URI lookup failed");
            TestRun.Check(await artwork.LoadAsync(fixture.DirectoryPath) is not null, "Directory lookup failed");
        });

        await tests.RunAsync("Oversize artwork falls through without decoding", async () =>
        {
            using var fixture = new ArtworkFixture();
            using (var file = File.Create(fixture.PathFor("movie.jpg"))) file.SetLength(33L * 1024 * 1024);
            fixture.WriteImage("poster.png", 80, 120);
            var image = (BitmapSource?)await artwork.LoadAsync(fixture.Media);
            TestRun.Check(image?.PixelHeight == 120, "Expected folder fallback for oversize candidate");
        });

        await tests.RunAsync("Artwork cancellation is observed", async () =>
        {
            using var fixture = new ArtworkFixture();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try
            {
                await artwork.LoadAsync(fixture.Media, cancellation.Token);
                throw new InvalidOperationException("Cancelled lookup completed successfully");
            }
            catch (OperationCanceledException) { }
        });

        tests.Run("Now Playing has at most four badges with PQ identified from metadata", () =>
        {
            var badges = MediaPresentation.Badges(@"C:\Films\Example.mkv",
                [Track(MediaTrackKind.Video, "hevc", "3840×2160 • 23.976 fps • HDR (PQ)"),
                 Track(MediaTrackKind.Audio, "eac3", "7.1"), Track(MediaTrackKind.Subtitle, "subrip")]);
            TestRun.Check(badges.SequenceEqual(new[] { "Local file", "3840×2160", "HDR · PQ", "DD+" }), "Unexpected badge contents/order");
            TestRun.Check(badges.Count <= 4, "Badge clutter exceeds four items");
        });

        tests.Run("HLG uses the reported transfer characteristic", () =>
        {
            var badges = MediaPresentation.Badges(@"Blu-ray disc • E:\ • title 2",
                [Track(MediaTrackKind.Video, "hevc", "3840×2160 • HDR (HLG)")]);
            TestRun.Check(badges.Contains("HDR · HLG") && !badges.Contains("HDR · PQ"), "HLG badge is incorrect");
        });

        tests.Run("HEVC, 4K, and TrueHD do not invent HDR, Dolby Vision, or Atmos", () =>
        {
            var badges = MediaPresentation.Badges(@"C:\Films\Example.mkv",
                [Track(MediaTrackKind.Video, "hevc", "3840×2160 • 23.976 fps"), Track(MediaTrackKind.Audio, "truehd", "7.1")]);
            var text = string.Join(" ", badges);
            TestRun.Check(!text.Contains("HDR") && !text.Contains("Vision") && !text.Contains("Atmos"), "An unreported format was inferred");
            TestRun.Check(badges.Contains("TrueHD"), "Reported audio codec should remain visible");
        });

        tests.Run("Selected video and audio streams determine badges", () =>
        {
            var badges = MediaPresentation.Badges(@"C:\Films\Example.mkv",
                [Track(MediaTrackKind.Video, "hevc", "3840×2160 • HDR (PQ)", selected: false),
                 Track(MediaTrackKind.Video, "h264", "1920×1080"),
                 Track(MediaTrackKind.Audio, "truehd", selected: false), Track(MediaTrackKind.Audio, "aac")]);
            TestRun.Check(badges.Contains("1920×1080") && badges.Contains("AAC"), "Selected streams were ignored");
            TestRun.Check(!badges.Contains("3840×2160") && !badges.Any(b => b.StartsWith("HDR")), "Inactive HDR stream leaked into presentation");
        });

        tests.Run("Optical source summaries retain the reported drive and title", () =>
        {
            foreach (var kind in new[] { "Blu-ray", "DVD" })
            {
                var source = $"{kind} disc • E:\\ • title 3";
                var summary = MediaPresentation.SourceSummary(source);
                TestRun.Check(MediaPresentation.SourceKind(source) == kind, "Optical kind was lost");
                TestRun.Check(summary.Contains(kind) && summary.Contains("E:\\") && summary.Contains("title 3", StringComparison.OrdinalIgnoreCase), "Optical source/title was lost");
            }
        });

        tests.Run("Local source summary keeps file type and containing folder without the full path", () =>
        {
            var summary = MediaPresentation.SourceSummary(@"C:\Films\Screening Room\Example.mkv");
            TestRun.Check(summary.Contains("MKV") && summary.Contains("Screening Room"), "Local context is missing");
            TestRun.Check(!summary.Contains("C:\\"), "Full path should be reserved for the source tooltip");
        });

        tests.Run("Empty metadata hides detail rows including unknown subtitle state", () =>
        {
            var rows = MediaPresentation.Details(PlaybackSnapshot.Disconnected(), [], []);
            TestRun.Check(rows.Count == 0, "Unknown metadata should leave detail rows empty");
        });

        tests.Run("Unknown runtime and chapter count are omitted while known metadata remains", () =>
        {
            var rows = MediaPresentation.Details(Snapshot(@"C:\Films\Example.mkv"),
                [Track(MediaTrackKind.Video, "h264", "1920×1080")], []);
            TestRun.Check(rows.Any(r => r.Label == "Video" && r.Value.Contains("H.264")), "Video metadata missing");
            TestRun.Check(!rows.Any(r => r.Label is "Runtime" or "Chapters"), "Unknown runtime or chapter count should be hidden");
        });

        tests.Run("Selected subtitles and disabled subtitles remain distinguishable", () =>
        {
            var selected = MediaPresentation.Details(Snapshot(@"C:\Films\Example.mkv"),
                [Track(MediaTrackKind.Subtitle, "subrip", language: "eng")], []);
            TestRun.Check(selected.Any(r => r.Label == "Subtitles" && r.Value.Contains("English")), "Selected subtitle metadata missing");
            var offTrack = new MediaTrack(0, MediaTrackKind.Subtitle, "Off", null, null, null, false, false, true);
            var disabled = MediaPresentation.Details(Snapshot(@"C:\Films\Example.mkv"), [offTrack], []);
            TestRun.Check(disabled.Any(r => r.Label == "Subtitles" && r.Value.Contains("Off")), "Disabled subtitle state missing");
        });

        tests.Run("Runtime formatting preserves hours above 24", () =>
        {
            var runtime = new TimeSpan(26, 3, 4);
            TestRun.Check(MediaPresentation.Time(runtime) == "26:03:04", "Runtime wrapped after 24 hours");
            var rows = MediaPresentation.Details(Snapshot(@"DVD disc • E:\ • title 1") with { Duration = runtime }, [], []);
            TestRun.Check(rows.Any(r => r.Label == "Runtime" && r.Value == "26:03:04"), "Detail runtime wrapped after 24 hours");
        });

        await tests.RunGroupAsync("Playback presentation races and UI checks", () => ViewModelChecks.RunAsync(args));
        return tests.Finish();
    }

    static MediaTrack Track(MediaTrackKind kind, string? codec, string? details = null, bool selected = true, string? language = null) =>
        new(1, kind, $"{kind} 1", language, codec, details, false, false, selected);

    static PlaybackSnapshot Snapshot(string source) =>
        new(PlaybackState.Playing, source, "Example", TimeSpan.Zero, TimeSpan.Zero, 68, false);

}

internal sealed class TestRun
{
    private int _passed;
    private int _failed;

    public void Run(string name, Action action)
    {
        try { action(); _passed++; }
        catch (Exception exception) { Fail(name, exception); }
    }

    public async Task RunAsync(string name, Func<Task> action)
    {
        try { await action(); _passed++; }
        catch (Exception exception) { Fail(name, exception); }
    }

    public async Task RunGroupAsync(string name, Func<Task<int>> action)
    {
        try { _passed += await action(); }
        catch (Exception exception) { Fail(name, exception); }
    }
    public static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private void Fail(string name, Exception exception)
    {
        _failed++;
        Console.Error.WriteLine($"FAIL: {name}: {exception.Message}");
    }

    public int Finish()
    {
        Console.WriteLine($"Presentation checks: {_passed} passed, {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }
}

internal sealed class ArtworkFixture : IDisposable
{
    // Every test owns a fresh directory under ignored build output. Dispose removes
    // only that fixture, allowing concurrent runs and leaving no media behind.
    public string DirectoryPath { get; } = Path.Combine(AppContext.BaseDirectory, "work", Guid.NewGuid().ToString("N"));
    public string Media => PathFor("movie.mkv");

    public ArtworkFixture()
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(Media, "Media path for artwork discovery only");
    }

    public string PathFor(string filename) => Path.Combine(DirectoryPath, filename);

    public void WriteImage(string filename, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var offset = 3; offset < pixels.Length; offset += 4) pixels[offset] = 255;
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        BitmapEncoder encoder = Path.GetExtension(filename).Equals(".png", StringComparison.OrdinalIgnoreCase)
            ? new PngBitmapEncoder() : new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(PathFor(filename));
        encoder.Save(stream);
    }

    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}
