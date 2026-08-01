using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using ParallaxStudio.App.Common;
using ParallaxStudio.App.Services;
using ParallaxStudio.Core;
using ParallaxStudio.Core.Enums;
using ParallaxStudio.Core.Events;
using ParallaxStudio.Core.Models;
using ParallaxStudio.Core.Services;

namespace ParallaxStudio.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IPlaybackService _playback;
    private readonly ISettingsService _settingsService;
    private readonly IResumeStore _resumeStore;
    private readonly IDiscService _discService;
    private readonly IAppLogger _logger;
    private readonly FilePickerService _filePicker;
    private readonly AppSettings _settings = new();
    private bool _initialized;
    private bool _shutdown;
    private string _statusText = "Preparing Parallax Studio...";
    private string _backendText = "Checking libmpv";
    private string _nowPlayingTitle = "Nothing playing";
    private string _sourceText = "Open a video to begin";
    private string _positionText = "00:00";
    private string _durationText = "00:00";
    private string _playPauseText = "Play";
    private double _volume = 68;
    private double _positionSeconds;
    private double _durationSeconds = 1;
    private bool _isTracksPanelOpen;
    private bool _isFullscreen;
    private bool _isMuted;
    private bool _isScrubbing;
    private double _scrubPosition;
    private string _selectedTheme = "Dark Monochrome";
    private string _selectedTypography = "Cadence";
    private double _subtitleDelay;
    private string _mediaDetailsText = "No media metadata loaded";
    private bool _isBluRayPanelOpen;
    private DiscSource? _activeBluRayDisc;
    private bool _isRefreshingMediaNavigation;

    public MainViewModel(
        IPlaybackService playback,
        ISettingsService settingsService,
        IResumeStore resumeStore,
        IDiscService discService,
        IAppLogger logger,
        FilePickerService filePicker)
    {
        _playback = playback;
        _settingsService = settingsService;
        _resumeStore = resumeStore;
        _discService = discService;
        _logger = logger;
        _filePicker = filePicker;

        _playback.StateChanged += OnPlaybackStateChanged;

        OpenCommand = new AsyncRelayCommand(OpenAsync);
        ScanDiscsCommand = new AsyncRelayCommand(ScanDiscsAsync);
        PlayDiscCommand = new AsyncRelayCommand<DiscSource>(PlayDiscAsync, disc => disc is { IsReady: true, Kind: DiscKind.BluRay });
        BrowseDiscTitlesCommand = new AsyncRelayCommand<DiscSource>(BrowseDiscTitlesAsync, disc => disc is { IsReady: true, Kind: DiscKind.BluRay });
        PlayBluRayTitleCommand = new AsyncRelayCommand<BluRayTitle>(PlayBluRayTitleAsync);
        ToggleBluRayPanelCommand = new RelayCommand(() => IsBluRayPanelOpen = !IsBluRayPanelOpen);
        PlayPauseCommand = new AsyncRelayCommand(PlayPauseAsync);
        StopCommand = new AsyncRelayCommand(StopAsync);
        SeekBackCommand = new AsyncRelayCommand(() => SeekAsync(-5));
        SeekForwardCommand = new AsyncRelayCommand(() => SeekAsync(5));
        ToggleTracksCommand = new RelayCommand(() => IsTracksPanelOpen = !IsTracksPanelOpen);
        ToggleFullscreenCommand = new RelayCommand(ToggleFullscreen);
        ToggleMuteCommand = new AsyncRelayCommand(ToggleMuteAsync);
        LoadSubtitleCommand = new AsyncRelayCommand(LoadExternalSubtitleAsync);
        SubtitleDelayBackCommand = new AsyncRelayCommand(() => AdjustSubtitleDelayAsync(-0.1));
        SubtitleDelayForwardCommand = new AsyncRelayCommand(() => AdjustSubtitleDelayAsync(0.1));
        PreviousChapterCommand = new AsyncRelayCommand(() => MoveChapterAsync(-1));
        NextChapterCommand = new AsyncRelayCommand(() => MoveChapterAsync(1));
    }

    public event EventHandler? ToggleFullscreenRequested;

    public string AppTitle => $"{AppIdentity.Name}  v{AppIdentity.Version}";
    public string VersionLabel => $"v{AppIdentity.Version}";
    public ObservableCollection<DiscSource> Discs { get; } = [];
    public ObservableCollection<MediaTrack> AudioTracks { get; } = [];
    public ObservableCollection<MediaTrack> SubtitleTracks { get; } = [];
    public ObservableCollection<MediaChapter> Chapters { get; } = [];
    public ObservableCollection<BluRayTitle> BluRayTitles { get; } = [];
    public bool IsRefreshingMediaNavigation => _isRefreshingMediaNavigation;
    public IReadOnlyList<string> ThemeNames => StudioAppearanceService.Themes;
    public IReadOnlyList<string> TypographyNames => StudioAppearanceService.TypographyProfiles;

    public ICommand OpenCommand { get; }
    public ICommand ScanDiscsCommand { get; }
    public ICommand PlayDiscCommand { get; }
    public ICommand BrowseDiscTitlesCommand { get; }
    public ICommand PlayBluRayTitleCommand { get; }
    public ICommand ToggleBluRayPanelCommand { get; }
    public ICommand PlayPauseCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand SeekBackCommand { get; }
    public ICommand SeekForwardCommand { get; }
    public ICommand ToggleTracksCommand { get; }
    public ICommand ToggleFullscreenCommand { get; }
    public ICommand ToggleMuteCommand { get; }
    public ICommand LoadSubtitleCommand { get; }
    public ICommand SubtitleDelayBackCommand { get; }
    public ICommand SubtitleDelayForwardCommand { get; }
    public ICommand PreviousChapterCommand { get; }
    public ICommand NextChapterCommand { get; }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string BackendText
    {
        get => _backendText;
        private set => SetProperty(ref _backendText, value);
    }

    public string NowPlayingTitle
    {
        get => _nowPlayingTitle;
        private set
        {
            if (!SetProperty(ref _nowPlayingTitle, value)) return;
            OnPropertyChanged(nameof(EmptyStateVisibility));
        }
    }

    public string SourceText
    {
        get => _sourceText;
        private set => SetProperty(ref _sourceText, value);
    }

    public string PositionText
    {
        get => _positionText;
        private set => SetProperty(ref _positionText, value);
    }

    public string DurationText
    {
        get => _durationText;
        private set => SetProperty(ref _durationText, value);
    }

    public string PlayPauseGlyph => PlayPauseText == "Pause" ? "\uE769" : "\uE768";
    public string MuteGlyph => IsMuted ? "\uE74F" : "\uE767";
    public Visibility EmptyStateVisibility => NowPlayingTitle == "Nothing playing" ? Visibility.Visible : Visibility.Collapsed;

    public double PositionSeconds
    {
        get => _positionSeconds;
        private set => SetProperty(ref _positionSeconds, value);
    }

    public double ScrubPosition
    {
        get => _scrubPosition;
        set => SetProperty(ref _scrubPosition, Math.Clamp(value, 0, DurationSeconds));
    }

    public bool IsScrubbing
    {
        get => _isScrubbing;
        set => SetProperty(ref _isScrubbing, value);
    }

    public bool IsMuted
    {
        get => _isMuted;
        private set
        {
            if (!SetProperty(ref _isMuted, value)) return;
            OnPropertyChanged(nameof(MuteGlyph));
        }
    }

    public double DurationSeconds
    {
        get => _durationSeconds;
        private set => SetProperty(ref _durationSeconds, Math.Max(1, value));
    }

    public string MediaDetailsText
    {
        get => _mediaDetailsText;
        private set => SetProperty(ref _mediaDetailsText, value);
    }

    public string SubtitleDelayText => $"{_subtitleDelay:+0.0;-0.0;0.0}s";

    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (!SetProperty(ref _selectedTheme, value)) return;
            _settings.ThemeName = value;
            StudioAppearanceService.ApplyTheme(value);
        }
    }

    public string SelectedTypography
    {
        get => _selectedTypography;
        set
        {
            if (!SetProperty(ref _selectedTypography, value)) return;
            _settings.TypographyProfile = value;
            StudioAppearanceService.ApplyTypography(value, _settings.TextSize);
        }
    }

    public string PlayPauseText
    {
        get => _playPauseText;
        private set
        {
            if (!SetProperty(ref _playPauseText, value)) return;
            OnPropertyChanged(nameof(PlayPauseGlyph));
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (!SetProperty(ref _volume, clamped)) return;
            _settings.Volume = clamped;
            _ = ApplyVolumeAsync(clamped);
        }
    }

    public bool IsTracksPanelOpen
    {
        get => _isTracksPanelOpen;
        set => SetProperty(ref _isTracksPanelOpen, value);
    }

    public bool IsBluRayPanelOpen
    {
        get => _isBluRayPanelOpen;
        set => SetProperty(ref _isBluRayPanelOpen, value);
    }

    public bool IsFullscreen
    {
        get => _isFullscreen;
        private set => SetProperty(ref _isFullscreen, value);
    }

    public async Task InitializeViewAsync()
    {
        if (_initialized) return;
        _initialized = true;

        var loaded = await _settingsService.LoadAsync();
        CopySettings(loaded, _settings);
        _volume = _settings.Volume;
        _selectedTheme = _settings.ThemeName;
        _selectedTypography = _settings.TypographyProfile;
        StudioAppearanceService.ApplyTheme(_selectedTheme);
        StudioAppearanceService.ApplyTypography(_selectedTypography, _settings.TextSize);
        OnPropertyChanged(nameof(Volume));
        OnPropertyChanged(nameof(SelectedTheme));
        OnPropertyChanged(nameof(SelectedTypography));

        StatusText = "Player shell ready. Waiting for the native video surface.";
        await ScanDiscsAsync();
    }

    public async Task AttachVideoAsync(nint handle)
    {
        try
        {
            StatusText = "Checking the libmpv backend...";
            var backend = await _playback.ProbeAsync();
            BackendText = backend.Description;

            if (!backend.IsAvailable)
            {
                StatusText = "libmpv-2.dll is missing. Copy it into the native folder, rebuild, and relaunch.";
                await _logger.WarningAsync(backend.Error ?? StatusText);
                return;
            }

            await _playback.InitializeAsync(handle);
            await _playback.SetVolumeAsync(Volume);
            BackendText = _playback.BackendDescription;
            StatusText = "libmpv connected. Open a local video to test the foundation build.";
        }
        catch (Exception ex)
        {
            BackendText = "libmpv unavailable";
            StatusText = ex.Message;
            await _logger.ErrorAsync("Could not attach libmpv to the WPF video surface.", ex);
        }
    }

    public void CaptureWindowState(double width, double height, double left, double top, bool isMaximized)
    {
        if (double.IsFinite(width) && width >= 760) _settings.WindowWidth = width;
        if (double.IsFinite(height) && height >= 520) _settings.WindowHeight = height;
        if (double.IsFinite(left)) _settings.WindowLeft = left;
        if (double.IsFinite(top)) _settings.WindowTop = top;
        _settings.IsMaximized = isMaximized;
    }

    public AppSettings GetWindowSettings() => _settings;

    public async Task ShutdownAsync()
    {
        if (_shutdown) return;
        _shutdown = true;

        // Detach before disposal so the polling thread can never block waiting
        // for the WPF dispatcher while the UI thread is shutting down.
        _playback.StateChanged -= OnPlaybackStateChanged;

        _settings.Volume = Volume;
        await _settingsService.SaveAsync(_settings).ConfigureAwait(false);
        await _playback.DisposeAsync().ConfigureAwait(false);
    }

    private async Task OpenAsync()
    {
        var path = _filePicker.PickMediaFile(_settings.LastOpenDirectory);
        if (path is null) return;
        await OpenPathAsync(path);
    }

    public async Task OpenPathAsync(string path)
    {
        try
        {
            _settings.LastOpenDirectory = Path.GetDirectoryName(path);
            StatusText = $"Opening {Path.GetFileName(path)}...";
            await _playback.OpenFileAsync(path);
            StatusText = "Playback active. Reading tracks, subtitles, and chapters...";
            await RefreshMediaNavigationWithRetryAsync(10, 300);
            StatusText = "Playback active. Tracks, subtitles, and chapters are ready.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            await _logger.ErrorAsync($"Could not open media: {path}", ex);
        }
    }

    public async Task SelectAudioTrackAsync(MediaTrack? track)
    {
        if (track is null) return;
        try
        {
            await _playback.SelectTrackAsync(track);
            await RefreshMediaNavigationAsync();
            StatusText = $"Audio track selected: {track.Label}.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    public async Task SelectSubtitleTrackAsync(MediaTrack? track)
    {
        if (track is null) return;
        try
        {
            await _playback.SelectTrackAsync(track);
            await RefreshMediaNavigationAsync();
            StatusText = track.Id <= 0 ? "Subtitles turned off." : $"Subtitle selected: {track.Label}.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    public async Task SelectChapterAsync(MediaChapter? chapter)
    {
        if (chapter is null) return;
        try
        {
            await _playback.SelectChapterAsync(chapter.Index);
            await Task.Delay(100);
            await RefreshMediaNavigationAsync();
            StatusText = $"Jumped to {chapter.Title}.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    public async Task CommitSeekAsync(double seconds)
    {
        try
        {
            await _playback.SeekAbsoluteAsync(seconds);
            StatusText = $"Seeked to {FormatTime(TimeSpan.FromSeconds(seconds))}.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private async Task<bool> RefreshMediaNavigationAsync()
    {
        var tracks = await _playback.GetTracksAsync();
        var chapters = await _playback.GetChaptersAsync();

        _isRefreshingMediaNavigation = true;
        try
        {
            AudioTracks.Clear();
            foreach (var track in tracks.Where(track => track.Kind == MediaTrackKind.Audio)) AudioTracks.Add(track);

            SubtitleTracks.Clear();
            SubtitleTracks.Add(new MediaTrack(0, MediaTrackKind.Subtitle, "Subtitles Off", null, null, null, false, false,
                !tracks.Any(track => track.Kind == MediaTrackKind.Subtitle && track.IsSelected)));
            foreach (var track in tracks.Where(track => track.Kind == MediaTrackKind.Subtitle)) SubtitleTracks.Add(track);

            Chapters.Clear();
            foreach (var chapter in chapters) Chapters.Add(chapter);
        }
        finally
        {
            _isRefreshingMediaNavigation = false;
        }

        var video = tracks.FirstOrDefault(track => track.Kind == MediaTrackKind.Video);
        MediaDetailsText = string.Join(" • ", new[]
        {
            video?.Codec,
            $"{AudioTracks.Count} audio",
            $"{Math.Max(0, SubtitleTracks.Count - 1)} subtitles",
            $"{Chapters.Count} chapters"
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        await _logger.InfoAsync($"Media navigation refreshed: {AudioTracks.Count} audio, {Math.Max(0, SubtitleTracks.Count - 1)} subtitles, {Chapters.Count} chapters.");
        return tracks.Count > 0 || chapters.Count > 0;
    }

    private async Task<bool> RefreshMediaNavigationWithRetryAsync(int attempts = 16, int delayMilliseconds = 500)
    {
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (await RefreshMediaNavigationAsync()) return true;
            if (_playback.Snapshot.State is PlaybackState.Faulted or PlaybackState.Stopped) return false;
            await Task.Delay(delayMilliseconds);
        }
        return false;
    }

    private async Task LoadExternalSubtitleAsync()
    {
        var path = _filePicker.PickSubtitleFile(_settings.LastOpenDirectory);
        if (path is null) return;
        try
        {
            await _playback.LoadExternalSubtitleAsync(path);
            await Task.Delay(200);
            await RefreshMediaNavigationAsync();
            StatusText = $"Loaded external subtitle: {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private async Task AdjustSubtitleDelayAsync(double delta)
    {
        _subtitleDelay = Math.Clamp(_subtitleDelay + delta, -10, 10);
        await _playback.SetSubtitleDelayAsync(_subtitleDelay);
        OnPropertyChanged(nameof(SubtitleDelayText));
        StatusText = $"Subtitle delay: {SubtitleDelayText}.";
    }

    private async Task MoveChapterAsync(int delta)
    {
        if (Chapters.Count == 0)
        {
            StatusText = "This media has no chapters.";
            return;
        }

        var current = Chapters.ToList().FindIndex(chapter => chapter.IsActive);
        if (current < 0) current = 0;
        var target = Math.Clamp(current + delta, 0, Chapters.Count - 1);
        await SelectChapterAsync(Chapters[target]);
    }

    private async Task ToggleMuteAsync()
    {
        try
        {
            await _playback.SetMutedAsync(!IsMuted);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private async Task PlayDiscAsync(DiscSource? disc)
    {
        if (disc is null) return;
        try
        {
            _activeBluRayDisc = disc;
            StatusText = $"Opening Blu-ray from {disc.DriveRoot}...";
            IsTracksPanelOpen = false;
            IsBluRayPanelOpen = false;
            if (!EnsureAacsConfigurationReady())
                return;

            await _playback.OpenBluRayAsync(disc);
            StatusText = "Blu-ray main feature is loading...";
            var navigationReady = await RefreshMediaNavigationWithRetryAsync();
            await RefreshBluRayTitlesAsync();
            if (_playback.Snapshot.State is PlaybackState.Faulted or PlaybackState.Stopped)
            {
                StatusText = FormatBluRayFailure(_playback.LastPlaybackError);
            }
            else
            {
                StatusText = navigationReady
                    ? "Blu-ray playback active. Audio, subtitles, and chapters are ready in Tracks."
                    : "Blu-ray playback active. Track metadata is still loading; open Tracks to refresh.";
            }
        }
        catch (Exception ex)
        {
            StatusText = "Blu-ray could not be opened. The disc may require AACS keys or unsupported protection.";
            await _logger.ErrorAsync($"Could not open Blu-ray from {disc.DriveRoot}", ex);
        }
    }

    private async Task BrowseDiscTitlesAsync(DiscSource? disc)
    {
        if (disc is null) return;
        _activeBluRayDisc = disc;
        IsTracksPanelOpen = false;

        try
        {
            if (!EnsureAacsConfigurationReady())
                return;

            StatusText = "Opening the Blu-ray so titles can be discovered...";
            await _playback.OpenBluRayAsync(disc);
            await Task.Delay(2200);
            await RefreshBluRayTitlesAsync();
            IsBluRayPanelOpen = true;
            StatusText = _playback.Snapshot.State is PlaybackState.Faulted or PlaybackState.Stopped
                ? FormatBluRayFailure(_playback.LastPlaybackError)
                : BluRayTitles.Count == 0
                    ? "The disc loaded, but libmpv did not report a title list."
                    : $"Found {BluRayTitles.Count} Blu-ray title{(BluRayTitles.Count == 1 ? string.Empty : "s")}.";
        }
        catch (Exception ex)
        {
            StatusText = "Blu-ray titles could not be read. Check AACS configuration or the playback log.";
            await _logger.ErrorAsync($"Could not browse Blu-ray titles from {disc.DriveRoot}", ex);
        }
    }

    private async Task PlayBluRayTitleAsync(BluRayTitle? title)
    {
        if (title is null || _activeBluRayDisc is null) return;
        try
        {
            StatusText = $"Opening Blu-ray title {title.DisplayNumber}...";
            await _playback.OpenBluRayTitleAsync(_activeBluRayDisc, title.Index);
            IsBluRayPanelOpen = false;
            await RefreshMediaNavigationWithRetryAsync();
            await RefreshBluRayTitlesAsync();
            StatusText = _playback.Snapshot.State is PlaybackState.Faulted or PlaybackState.Stopped
                ? FormatBluRayFailure(_playback.LastPlaybackError)
                : $"Blu-ray title {title.DisplayNumber} is playing.";
        }
        catch (Exception ex)
        {
            StatusText = $"Blu-ray title {title.DisplayNumber} could not be opened.";
            await _logger.ErrorAsync(StatusText, ex);
        }
    }


    private bool EnsureAacsConfigurationReady()
    {
        if (_playback.IsMakeMkvLibMmbdAvailable())
        {
            StatusText = $"Protected Blu-ray provider ready: {_playback.DiscDecryptionProvider}.";
            return true;
        }

        var keyDb = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "aacs",
            "KEYDB.cfg");

        string? message = null;
        if (!File.Exists(keyDb))
        {
            message = $"KEYDB.cfg was not found.\n\nExpected location:\n{keyDb}\n\nAdd valid AACS configuration for a Blu-ray you own, then try again.";
            StatusText = $"Protected Blu-ray requires AACS configuration. KEYDB.cfg was not found at {keyDb}.";
        }
        else if (new FileInfo(keyDb).Length == 0)
        {
            message = $"KEYDB.cfg was found, but it is empty.\n\nLocation:\n{keyDb}\n\nAdd valid AACS configuration for a Blu-ray you own, then try again.";
            StatusText = "KEYDB.cfg was found, but it is empty. This protected Blu-ray needs matching AACS configuration.";
        }

        if (message is null)
            return true;

        MessageBox.Show(
            message,
            "Protected Blu-ray cannot be opened",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return false;
    }

    private static string FormatBluRayFailure(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return "Blu-ray loading failed. Open the Parallax Studio log for details.";

        if (error.Contains("no matching processing key", StringComparison.OrdinalIgnoreCase))
            return "Protected Blu-ray could not be opened: KEYDB.cfg has no matching processing key for this disc.";
        if (error.Contains("missing config file", StringComparison.OrdinalIgnoreCase))
            return "Protected Blu-ray could not be opened: the AACS KEYDB.cfg file is missing.";
        if (error.Contains("certificate", StringComparison.OrdinalIgnoreCase))
            return "Protected Blu-ray could not be opened: libaacs could not obtain the required certificate or key.";

        return $"Blu-ray loading failed: {error}";
    }

    private async Task RefreshBluRayTitlesAsync()
    {
        var titles = await _playback.GetBluRayTitlesAsync();
        BluRayTitles.Clear();
        foreach (var title in titles) BluRayTitles.Add(title);
    }

    private async Task ScanDiscsAsync()
    {
        try
        {
            var discs = await _discService.GetAvailableDiscsAsync();
            Discs.Clear();
            foreach (var disc in discs) Discs.Add(disc);
            StatusText = discs.Count == 0
                ? "No optical drives detected. File playback remains available."
                : $"Found {discs.Count} optical drive{(discs.Count == 1 ? string.Empty : "s")}. Insert a Blu-ray and choose Play Disc.";
        }
        catch (Exception ex)
        {
            StatusText = "Optical-drive scan failed.";
            await _logger.ErrorAsync(StatusText, ex);
        }
    }

    private async Task PlayPauseAsync()
    {
        try
        {
            await _playback.TogglePauseAsync();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private async Task StopAsync()
    {
        try
        {
            await _playback.StopAsync();
            StatusText = "Playback stopped.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private async Task SeekAsync(double seconds)
    {
        try
        {
            await _playback.SeekRelativeAsync(seconds);
            StatusText = $"Seeked {(seconds < 0 ? "back" : "forward")} {Math.Abs(seconds):0} seconds.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private async Task ApplyVolumeAsync(double value)
    {
        try
        {
            await _playback.SetVolumeAsync(value);
        }
        catch (Exception ex)
        {
            await _logger.WarningAsync($"Could not apply volume: {ex.Message}");
        }
    }

    private void ToggleFullscreen()
    {
        IsFullscreen = !IsFullscreen;
        ToggleFullscreenRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnPlaybackStateChanged(object? sender, PlaybackStateChangedEventArgs e)
    {
        void Apply()
        {
            var snapshot = e.Snapshot;
            NowPlayingTitle = snapshot.Title;
            SourceText = snapshot.Source ?? "Open a video to begin";
            PositionText = FormatTime(snapshot.Position);
            DurationText = FormatTime(snapshot.Duration);
            PositionSeconds = snapshot.Position.TotalSeconds;
            DurationSeconds = snapshot.Duration.TotalSeconds;
            if (!IsScrubbing) ScrubPosition = snapshot.Position.TotalSeconds;
            IsMuted = snapshot.IsMuted;
            PlayPauseText = snapshot.State == PlaybackState.Paused ? "Play" : "Pause";
        }

        if (_shutdown || Application.Current is null) return;

        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            Apply();
        }
        else if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
        {
            // Never synchronously block the playback polling thread on the UI
            // dispatcher. Doing so can deadlock application shutdown.
            _ = dispatcher.BeginInvoke(Apply);
        }
    }

    private static string FormatTime(TimeSpan value) =>
        value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"mm\:ss");

    private static void CopySettings(AppSettings source, AppSettings destination)
    {
        destination.SchemaVersion = source.SchemaVersion;
        destination.Volume = Math.Clamp(source.Volume, 0, 100);
        destination.LastOpenDirectory = source.LastOpenDirectory;
        destination.WindowWidth = source.WindowWidth;
        destination.WindowHeight = source.WindowHeight;
        destination.WindowLeft = source.WindowLeft;
        destination.WindowTop = source.WindowTop;
        destination.IsMaximized = source.IsMaximized;
        destination.ThemeName = string.IsNullOrWhiteSpace(source.ThemeName) ? "Dark Monochrome" : source.ThemeName;
        destination.TypographyProfile = string.IsNullOrWhiteSpace(source.TypographyProfile) ? "Cadence" : source.TypographyProfile;
        destination.TextSize = string.IsNullOrWhiteSpace(source.TextSize) ? "Standard" : source.TextSize;
        destination.ReduceMotion = source.ReduceMotion;
    }
}
