using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ParallaxStudio.App;
using ParallaxStudio.App.Services;
using ParallaxStudio.App.ViewModels;
using ParallaxStudio.Core.Enums;
using ParallaxStudio.Core.Events;
using ParallaxStudio.Core.Models;
using ParallaxStudio.Core.Services;

internal static class ViewModelChecks
{
    public static async Task<int> RunAsync(string[] args)
    {
        var passed = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            passed++;
        }
        var playback = DispatchProxy.Create<IPlaybackService, PlaybackDouble>();
        var backend = (PlaybackDouble)(object)playback;
        var settings = new SettingsDouble();
        var vm = new MainViewModel(playback, settings, new ResumeDouble(), new DiscDouble(), new LoggerDouble(), new FilePickerService());
        try
        {
            await vm.InitializeViewAsync();
            Check(vm.InterfaceDensity == "Comfortable", "Old settings must default to comfortable density");
            Check(StudioAppearanceService.Motion.ReduceMotion == !SystemParameters.ClientAreaAnimation, "Initial motion must respect Windows animation preference");
            var comfortableSidebarWidth = (double)Application.Current.Resources["StudioSidebarWidth"];
            var comfortableButtonSize = (double)Application.Current.Resources["StudioIconButtonSize"];
            vm.InterfaceDensity = "Compact";
            vm.ReduceMotion = true;
            Check(settings.Saved?.InterfaceDensity == "Compact" && settings.Saved.ReduceMotion, "Appearance settings must persist");
            Check(StudioAppearanceService.Motion.ReduceMotion, "Reduced motion must reach the shared control preference");
            var motionButton = new Button { Style = (Style)Application.Current.Resources["CommandButton"] };
            motionButton.ApplyTemplate();
            var reducedTemplate = Application.Current.Resources["ReducedMotionCommandButtonTemplate"];
            Check(ReferenceEquals(motionButton.Template, reducedTemplate), "Reduced motion must select the immediate-feedback control template");
            vm.ReduceMotion = false;
            await Task.Delay(10);
            motionButton.ApplyTemplate();
            Check(StudioAppearanceService.Motion.ReduceMotion == !SystemParameters.ClientAreaAnimation, "Disabling app reduced motion must still respect Windows");
            if (SystemParameters.ClientAreaAnimation)
                Check(!ReferenceEquals(motionButton.Template, reducedTemplate), "Standard motion must restore the animated control template");
            vm.ReduceMotion = true;
            Check((double)Application.Current.Resources["StudioSidebarWidth"] < comfortableSidebarWidth &&
                (double)Application.Current.Resources["StudioIconButtonSize"] < comfortableButtonSize, "Compact density must reduce sidebar and transport dimensions");
            vm.InterfaceDensity = "Comfortable";
            Check((double)Application.Current.Resources["StudioSidebarWidth"] == comfortableSidebarWidth &&
                (double)Application.Current.Resources["StudioIconButtonSize"] == comfortableButtonSize, "Comfortable density must restore layout dimensions");
            vm.InterfaceDensity = "Compact";

            const string first = @"C:\presentation-fixture\first.mkv";
            const string second = @"C:\presentation-fixture\second.mkv";
            backend.Tracks = Tracks("1920×1080 • 24 fps", "aac");
            backend.Publish(Playing(first, "First film"));
            await Task.Delay(250);
            Check(vm.MediaBadges.Contains("1920×1080"), "First ready snapshot must refresh metadata with the title browser closed");

            using (var fixture = new ArtworkFixture())
            {
                fixture.WriteImage("movie.png", 100, 150);
                backend.Publish(Playing(fixture.Media, "Film with local art"));
                for (var attempt = 0; attempt < 40 && vm.Artwork is null; attempt++) await Task.Delay(25);
                Check(vm.Artwork?.IsFrozen == true, "Artwork must reach the view model as a frozen image");
                backend.Publish(Playing(first, "First film"));
                Check(vm.Artwork is null, "Switching source must clear its predecessor's artwork immediately");
                backend.Publish(Playing(fixture.Media, "Fast switch"));
                backend.Publish(Playing(first, "First film"));
                await Task.Delay(250);
                Check(vm.Artwork is null, "Late artwork from a superseded source must be discarded");
            }

            // Hold an old source's navigation read while a new source loads.
            var oldRead = new TaskCompletionSource<IReadOnlyList<MediaTrack>>();
            backend.NextTrackRead = oldRead.Task;
            var staleRefresh = vm.SelectAudioTrackAsync(backend.Tracks[1]);
            backend.Publish(Playing(second, "Second film") with { State = PlaybackState.Loading, Duration = TimeSpan.Zero });
            Check(vm.MediaBadges.SequenceEqual(new[] { "Local file" }) && vm.AudioTracks.Count == 0, "Loading a new source must clear old badges and track choices");
            Check(vm.ProgressText == "Reading runtime…", "Unknown runtime needs a placeholder");
            backend.Tracks = Tracks("3840×2160 • 23.976 fps • HDR (PQ)", "truehd");
            backend.Publish(Playing(second, "Second film"));
            oldRead.SetResult(Tracks("1920×1080 • 24 fps", "aac"));
            await staleRefresh;
            await Task.Delay(250);
            Check(vm.MediaBadges.Contains("3840×2160") && !vm.MediaBadges.Contains("1920×1080"), "Stale reads must not overwrite a new source");
            Check(vm.MediaBadges.Contains("HDR · PQ") && vm.MediaBadges.Contains("TrueHD"), "Badges must follow the selected streams");

            backend.Publish(Playing(second, "Second film") with { State = PlaybackState.Paused, Position = TimeSpan.FromMinutes(30) });
            Check(vm.NowPlayingStateText == "PAUSED" && Math.Abs(vm.NowPlayingProgress - 50) < .01, "Paused progress must remain accurate");
            Check(vm.MediaBadges.Contains("HDR · PQ"), "Pause and seek must preserve metadata");

            // A failed open before Loading must preserve the paused presentation.
            backend.OpenFailure = new IOException("Synthetic open failure");
            await vm.OpenPathAsync(@"C:\presentation-fixture\missing.mkv");
            Check(vm.MediaBadges.Contains("3840×2160") && vm.NowPlayingTitle == "Second film", "Rejected opens must preserve the current media presentation");
            backend.OpenFailure = null;

            // Decoder properties can arrive after the track list and container FPS.
            const string optical = "Blu-ray disc • E:\\ • title 2";
            backend.Tracks = Tracks("23.976 fps", "dca");
            backend.Publish(Playing(optical, "Disc — Title 2") with { State = PlaybackState.Loading });
            backend.Publish(Playing(optical, "Disc — Title 2"));
            await Task.Delay(300);
            backend.Tracks = Tracks("3840×2160 • 23.976 fps • HDR (HLG)", "dca");
            await Task.Delay(850);
            Check(vm.MediaBadges.Contains("HDR · HLG") && vm.MediaBadges.Contains("DTS"), "Later decoder metadata must replace FPS-only details without guessing DTS-HD");
            Check(backend.DiscoveryCalls == 0, "Metadata presentation must never trigger optical title discovery");
            backend.Publish(Playing("DVD disc • E:\\ • DVD title 4", "Disc — Program") with { State = PlaybackState.Loading });
            backend.Publish(Playing("DVD disc • E:\\ • DVD title 4", "Disc — Program"));
            await Task.Delay(250);
            Check(vm.SourceText == "DVD · E:\\ · DVD title 4", "DVD presentation must use the backend's reconciled title number");

            var previewIndex = Array.IndexOf(args, "--preview");
            await RenderChecksAsync(vm, backend, previewIndex >= 0 && previewIndex + 1 < args.Length
                ? Path.GetFullPath(args[previewIndex + 1]) : null);
            passed++;

            backend.Publish(backend.Snapshot with { State = PlaybackState.Stopped });
            Check(vm.Artwork is null && vm.MediaBadges.Count == 0 && vm.MediaDetails.Count == 0, "Stop must clear media artwork and details");
            Check(vm.NowPlayingProgress == 0 && vm.NowPlayingStateText == "READY", "Stop must return to the ready presentation");
        }
        finally { await vm.ShutdownAsync(); }
        return passed;
    }

    private static async Task RenderChecksAsync(MainViewModel vm, PlaybackDouble backend, string? directory)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        vm.CinemaAutoExpand = false;
        backend.Tracks = Tracks("3840×2160 • 23.976 fps • HDR (PQ)", "truehd");
        backend.Publish(Playing(@"C:\presentation-fixture\The Long Way Home.mkv", "The Long Way Home") with { Position = TimeSpan.FromMinutes(23) });
        await vm.OpenPathAsync(@"C:\presentation-fixture\The Long Way Home.mkv");
        await Task.Delay(250);
        var errors = new BindingErrors();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        var window = new MainWindow(vm);
        try
        {
            var content = (FrameworkElement)window.Content;
            foreach (var density in new[] { "Comfortable", "Compact" })
            foreach (var theme in StudioAppearanceService.Themes)
            {
                vm.InterfaceDensity = density;
                vm.SelectedTheme = theme;
                content.Measure(new Size(1380, 860));
                content.Arrange(new Rect(0, 0, 1380, 860));
                content.UpdateLayout();
                if (directory is null) continue;
                var bitmap = new RenderTargetBitmap(1380, 860, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(directory, $"{theme.Replace(' ', '-')}-{density}.png"));
                encoder.Save(file);
            }
            vm.IsSettingsPanelOpen = true;
            content.Measure(new Size(1020, 680));
            content.Arrange(new Rect(0, 0, 1020, 680));
            content.UpdateLayout();
            if (directory is not null)
            {
                var settingsBitmap = new RenderTargetBitmap(1020, 680, 96, 96, PixelFormats.Pbgra32);
                settingsBitmap.Render(content);
                var settingsEncoder = new PngBitmapEncoder();
                settingsEncoder.Frames.Add(BitmapFrame.Create(settingsBitmap));
                using var file = File.Create(Path.Combine(directory, "Settings-minimum.png"));
                settingsEncoder.Save(file);
            }
            if (errors.Messages.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors.Messages));
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
            // Never show the window or create/attach a native playback surface.
            window.Content = null;
        }
    }

    private static PlaybackSnapshot Playing(string source, string title) => new(PlaybackState.Playing, source, title,
        TimeSpan.Zero, TimeSpan.FromHours(1), 68, false);
    private static IReadOnlyList<MediaTrack> Tracks(string video, string audio) =>
    [
        new(1, MediaTrackKind.Video, "Video 1", null, "hevc", video, true, false, true),
        new(2, MediaTrackKind.Audio, "Audio 2", "eng", audio, "7.1 • 48 kHz", true, false, true)
    ];

    private sealed class BindingErrors : TraceListener
    {
        public List<string> Messages { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
    private sealed class SettingsDouble : ISettingsService
    {
        public AppSettings? Saved { get; private set; }
        public Task<AppSettings> LoadAsync(CancellationToken token = default) => Task.FromResult(JsonSerializer.Deserialize<AppSettings>("{\"SchemaVersion\":4}")!);
        public Task SaveAsync(AppSettings settings, CancellationToken token = default)
        { Saved = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings)); return Task.CompletedTask; }
    }
    private sealed class ResumeDouble : IResumeStore
    {
        public Task<ResumeEntry?> GetAsync(string path, CancellationToken token = default) => Task.FromResult<ResumeEntry?>(null);
        public Task UpsertAsync(ResumeEntry entry, CancellationToken token = default) => Task.CompletedTask;
        public Task RemoveAsync(string path, CancellationToken token = default) => Task.CompletedTask;
    }
    private sealed class DiscDouble : IDiscService
    {
        public Task<IReadOnlyList<DiscSource>> GetAvailableDiscsAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<DiscSource>>([]);
    }
    private sealed class LoggerDouble : IAppLogger
    {
        public Task InfoAsync(string message, CancellationToken token = default) => Task.CompletedTask;
        public Task WarningAsync(string message, CancellationToken token = default) => Task.CompletedTask;
        public Task ErrorAsync(string message, Exception? exception = null, CancellationToken token = default) => Task.CompletedTask;
    }
}

// DispatchProxy keeps the double focused on presentation; unused playback methods
// have no native side effects and unexpected additions fail explicitly.
public class PlaybackDouble : DispatchProxy
{
    private EventHandler<PlaybackStateChangedEventArgs>? _changed;
    public PlaybackSnapshot Snapshot { get; private set; } = PlaybackSnapshot.Disconnected();
    public IReadOnlyList<MediaTrack> Tracks { get; set; } = [];
    public Task<IReadOnlyList<MediaTrack>>? NextTrackRead { get; set; }
    public Exception? OpenFailure { get; set; }
    public int DiscoveryCalls { get; private set; }
    public void Publish(PlaybackSnapshot snapshot) { Snapshot = snapshot; _changed?.Invoke(this, new(snapshot)); }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case "add_StateChanged": _changed += (EventHandler<PlaybackStateChangedEventArgs>)args![0]!; return null;
            case "remove_StateChanged": _changed -= (EventHandler<PlaybackStateChangedEventArgs>)args![0]!; return null;
            case "get_Snapshot": return Snapshot;
            case "get_IsInitialized": return true;
            case "get_BackendDescription": return "Presentation test double";
            case "get_LastPlaybackError": return null;
            case "get_DiscDecryptionProvider": return "Test double";
            case "IsMakeMkvLibMmbdAvailable": return false;
            case "ProbeAsync": return Task.FromResult(new PlaybackBackendStatus(false, "Presentation tests"));
            case "GetTracksAsync":
                var pending = NextTrackRead;
                NextTrackRead = null;
                return pending ?? Task.FromResult(Tracks);
            case "GetChaptersAsync": return Task.FromResult<IReadOnlyList<MediaChapter>>([]);
            case "GetBluRayTitlesAsync": case "GetDvdTitlesAsync":
                DiscoveryCalls++; return Task.FromResult<IReadOnlyList<BluRayTitle>>([]);
            case "OpenFileAsync": return OpenFailure is null ? Task.CompletedTask : Task.FromException(OpenFailure);
            case "DisposeAsync": return ValueTask.CompletedTask;
            default:
                if (method.ReturnType == typeof(Task)) return Task.CompletedTask;
                throw new NotSupportedException(method.Name);
        }
    }
}
