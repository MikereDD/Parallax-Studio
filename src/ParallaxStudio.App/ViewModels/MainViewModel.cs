using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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
    private string _mediaDetailsText = "Open a video or play a disc to see codec, format, audio, subtitle, and chapter details.";
    private bool _isBluRayPanelOpen;
    private DiscSource? _activeDisc;
    private bool _isRefreshingMediaNavigation;
    private bool _isBluRayTitleDiscoveryBusy;
    private string _bluRayTitleSummary = "Select a Blu-ray or DVD to discover titles, episodes, playlists, and extras.";
    private BluRayTitle? _selectedBluRayTitle;
    private readonly SemaphoreSlim _discScanGate = new(1, 1);
    private readonly CancellationTokenSource _discWatchCancellation = new();
    private CancellationTokenSource? _bluRayDiscoveryCancellation;
    private Task? _discWatchTask;
    private string _discSnapshotKey = string.Empty;
    private readonly RelayCommand _cancelBluRayTitleDiscoveryCommand;
    private bool _dvdReconciliationUiRefreshPending;
    private string _lastDvdReconciliationUiSource = string.Empty;
    private readonly SemaphoreSlim _settingsSaveGate = new(1, 1);
    private bool _isSettingsPanelOpen;
    private bool _isCinemaSidebarHidden;
    private PlaybackState _currentPlaybackState = PlaybackState.Stopped;
    private bool? _sidebarHiddenOverride;
    private string? _activeResumePath;
    private DateTimeOffset _lastResumePersistUtc = DateTimeOffset.MinValue;
    private bool _hasActiveMedia;
    private readonly LocalArtworkService _artworkService = new();
    private readonly TmdbArtworkService _tmdbArtworkService = new();
    private CancellationTokenSource? _artworkCancellation;
    private int _artworkLookupGeneration = -1;
    private string? _artworkLookupTitle;
    private TimeSpan _artworkLookupDuration;
    private long _artworkRequestId;
    private ImageSource? _artwork;
    private string? _presentationSource;
    private int _mediaGeneration;
    private int _automaticRefreshGeneration = -1;
    private IReadOnlyList<MediaTrack> _presentationTracks = [];
    private IReadOnlyList<MediaChapter> _presentationChapters = [];
    private string _metadataStatus = "Open media to see its details.";
    private string _nowPlayingStateText = "READY";
    private double _nowPlayingProgress;
    private string _progressText = "Ready for your next screening";
    private string _remainingText = string.Empty;

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

        _cancelBluRayTitleDiscoveryCommand = new RelayCommand(
            CancelBluRayTitleDiscovery,
            () => IsBluRayTitleDiscoveryBusy);

        OpenCommand = new AsyncRelayCommand(OpenAsync);
        ScanDiscsCommand = new AsyncRelayCommand(ScanDiscsAsync);
        PlayDiscCommand = new AsyncRelayCommand<DiscSource>(PlayDiscAsync, disc => disc is { IsReady: true } && (disc.Kind == DiscKind.BluRay || disc.Kind == DiscKind.Dvd));
        BrowseDiscTitlesCommand = new AsyncRelayCommand<DiscSource>(BrowseDiscTitlesAsync, disc => disc is { IsReady: true } && (disc.Kind == DiscKind.BluRay || disc.Kind == DiscKind.Dvd));
        PlayBluRayTitleCommand = new AsyncRelayCommand<BluRayTitle>(PlayBluRayTitleAsync);
        CancelBluRayTitleDiscoveryCommand = _cancelBluRayTitleDiscoveryCommand;
        ToggleBluRayPanelCommand = new RelayCommand(ToggleBluRayPanel);
        PlayPauseCommand = new AsyncRelayCommand(PlayPauseAsync);
        StopCommand = new AsyncRelayCommand(StopAsync);
        SeekBackCommand = new AsyncRelayCommand(() => SeekAsync(-SeekStepSeconds));
        SeekForwardCommand = new AsyncRelayCommand(() => SeekAsync(SeekStepSeconds));
        ToggleTracksCommand = new AsyncRelayCommand(ToggleTracksAsync);
        ToggleFullscreenCommand = new RelayCommand(ToggleFullscreen);
        ToggleSettingsCommand = new RelayCommand(ToggleSettingsPanel);
        ToggleSidebarCommand = new RelayCommand(ToggleSidebarVisibility);
        ToggleMuteCommand = new AsyncRelayCommand(ToggleMuteAsync);
        LoadSubtitleCommand = new AsyncRelayCommand(LoadExternalSubtitleAsync);
        SubtitleDelayBackCommand = new AsyncRelayCommand(() => AdjustSubtitleDelayAsync(-0.1));
        SubtitleDelayForwardCommand = new AsyncRelayCommand(() => AdjustSubtitleDelayAsync(0.1));
        PreviousChapterCommand = new AsyncRelayCommand(() => MoveChapterAsync(-1));
        NextChapterCommand = new AsyncRelayCommand(() => MoveChapterAsync(1));
    }

    public event EventHandler? ToggleFullscreenRequested;
    public event EventHandler? CinemaSettingsChanged;
    public event EventHandler? MediaPresentationChanged;

    public string AppTitle => $"{AppIdentity.Name}  v{AppIdentity.Version}";
    public string VersionLabel => $"v{AppIdentity.Version}";
    public ObservableCollection<DiscSource> Discs { get; } = [];
    public ObservableCollection<MediaTrack> AudioTracks { get; } = [];
    public ObservableCollection<MediaTrack> SubtitleTracks { get; } = [];
    public ObservableCollection<MediaChapter> Chapters { get; } = [];
    public ObservableCollection<BluRayTitle> BluRayTitles { get; } = [];
    public ObservableCollection<string> MediaBadges { get; } = [];
    public ObservableCollection<MediaDetailRow> MediaDetails { get; } = [];
    public ImageSource? Artwork { get => _artwork; private set => SetProperty(ref _artwork, value); }
    public string SourceLocation => _presentationSource ?? string.Empty;
    public string MetadataStatus { get => _metadataStatus; private set => SetProperty(ref _metadataStatus, value); }
    public string NowPlayingStateText { get => _nowPlayingStateText; private set => SetProperty(ref _nowPlayingStateText, value); }
    public double NowPlayingProgress { get => _nowPlayingProgress; private set => SetProperty(ref _nowPlayingProgress, value); }
    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }
    public string RemainingText { get => _remainingText; private set => SetProperty(ref _remainingText, value); }
    public bool IsRefreshingMediaNavigation => _isRefreshingMediaNavigation;
    public bool IsBluRayTitleDiscoveryBusy
    {
        get => _isBluRayTitleDiscoveryBusy;
        private set
        {
            if (!SetProperty(ref _isBluRayTitleDiscoveryBusy, value)) return;
            _cancelBluRayTitleDiscoveryCommand.RaiseCanExecuteChanged();
        }
    }
    public string BluRayTitleSummary
    {
        get => _bluRayTitleSummary;
        private set => SetProperty(ref _bluRayTitleSummary, value);
    }
    public BluRayTitle? SelectedBluRayTitle
    {
        get => _selectedBluRayTitle;
        set => SetProperty(ref _selectedBluRayTitle, value);
    }
    public IReadOnlyList<string> ThemeNames => StudioAppearanceService.Themes;
    public IReadOnlyList<string> TypographyNames => StudioAppearanceService.TypographyProfiles;
    public IReadOnlyList<string> InterfaceDensities => StudioAppearanceService.InterfaceDensities;
    public IReadOnlyList<int> FullscreenControlTimeoutOptions { get; } = [2, 3, 4, 5, 8];
    public IReadOnlyList<int> SeekStepOptions { get; } = [5, 10, 15, 30];
    public string SeekBackToolTip => $"Back {SeekStepSeconds} seconds (←)";
    public string SeekForwardToolTip => $"Forward {SeekStepSeconds} seconds (→)";

    public ICommand OpenCommand { get; }
    public ICommand ScanDiscsCommand { get; }
    public ICommand PlayDiscCommand { get; }
    public ICommand BrowseDiscTitlesCommand { get; }
    public ICommand PlayBluRayTitleCommand { get; }
    public ICommand CancelBluRayTitleDiscoveryCommand { get; }
    public ICommand ToggleBluRayPanelCommand { get; }
    public ICommand PlayPauseCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand SeekBackCommand { get; }
    public ICommand SeekForwardCommand { get; }
    public ICommand ToggleTracksCommand { get; }
    public ICommand ToggleFullscreenCommand { get; }
    public ICommand ToggleSettingsCommand { get; }
    public ICommand ToggleSidebarCommand { get; }
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

    public bool HasActiveMedia
    {
        get => _hasActiveMedia;
        private set
        {
            if (!SetProperty(ref _hasActiveMedia, value)) return;
            OnPropertyChanged(nameof(EmptyStateVisibility));
            MediaPresentationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public Visibility EmptyStateVisibility => HasActiveMedia ? Visibility.Collapsed : Visibility.Visible;

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
            QueueSettingsSave();
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
            QueueSettingsSave();
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
            if (_settings.RememberVolume)
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

    public bool IsSettingsPanelOpen
    {
        get => _isSettingsPanelOpen;
        set => SetProperty(ref _isSettingsPanelOpen, value);
    }

    public bool IsCinemaSidebarHidden
    {
        get => _isCinemaSidebarHidden;
        private set => SetProperty(ref _isCinemaSidebarHidden, value);
    }

    public bool CinemaAutoExpand
    {
        get => _settings.CinemaAutoExpand;
        set => UpdateCinemaSetting(_settings.CinemaAutoExpand, value, v => _settings.CinemaAutoExpand = v, nameof(CinemaAutoExpand), refreshSidebar: true);
    }

    public bool RestoreSidebarWhenPlaybackStops
    {
        get => _settings.RestoreSidebarWhenPlaybackStops;
        set => UpdateCinemaSetting(_settings.RestoreSidebarWhenPlaybackStops, value, v => _settings.RestoreSidebarWhenPlaybackStops = v, nameof(RestoreSidebarWhenPlaybackStops), refreshSidebar: true);
    }

    public bool ShowSidebarWhenPaused
    {
        get => _settings.ShowSidebarWhenPaused;
        set => UpdateCinemaSetting(_settings.ShowSidebarWhenPaused, value, v => _settings.ShowSidebarWhenPaused = v, nameof(ShowSidebarWhenPaused), refreshSidebar: true);
    }

    public bool FullscreenHideTransport
    {
        get => _settings.FullscreenHideTransport;
        set => UpdateCinemaSetting(_settings.FullscreenHideTransport, value, v => _settings.FullscreenHideTransport = v, nameof(FullscreenHideTransport));
    }

    public bool FullscreenShowControlsOnMouseMove
    {
        get => _settings.FullscreenShowControlsOnMouseMove;
        set => UpdateCinemaSetting(_settings.FullscreenShowControlsOnMouseMove, value, v => _settings.FullscreenShowControlsOnMouseMove = v, nameof(FullscreenShowControlsOnMouseMove));
    }

    public int FullscreenControlsAutoHideSeconds
    {
        get => _settings.FullscreenControlsAutoHideSeconds;
        set
        {
            var clamped = Math.Clamp(value, 1, 15);
            if (_settings.FullscreenControlsAutoHideSeconds == clamped) return;
            _settings.FullscreenControlsAutoHideSeconds = clamped;
            OnPropertyChanged();
            CinemaSettingsChanged?.Invoke(this, EventArgs.Empty);
            QueueSettingsSave();
        }
    }

    public bool FullscreenCursorAutoHide
    {
        get => _settings.FullscreenCursorAutoHide;
        set => UpdateCinemaSetting(_settings.FullscreenCursorAutoHide, value, v => _settings.FullscreenCursorAutoHide = v, nameof(FullscreenCursorAutoHide));
    }

    public bool EscapeExitsFullscreen
    {
        get => _settings.EscapeExitsFullscreen;
        set => UpdateCinemaSetting(_settings.EscapeExitsFullscreen, value, v => _settings.EscapeExitsFullscreen = v, nameof(EscapeExitsFullscreen));
    }

    public bool RememberWindowGeometry
    {
        get => _settings.RememberWindowGeometry;
        set => UpdateCinemaSetting(_settings.RememberWindowGeometry, value, v => _settings.RememberWindowGeometry = v, nameof(RememberWindowGeometry));
    }

    public bool ReduceMotion
    {
        get => _settings.ReduceMotion;
        set
        {
            UpdateCinemaSetting(_settings.ReduceMotion, value, v => _settings.ReduceMotion = v, nameof(ReduceMotion));
            StudioAppearanceService.ApplyMotion(value);
        }
    }

    public string InterfaceDensity
    {
        get => _settings.InterfaceDensity;
        set
        {
            var normalized = value == "Compact" ? "Compact" : "Comfortable";
            if (_settings.InterfaceDensity == normalized) return;
            _settings.InterfaceDensity = normalized;
            StudioAppearanceService.ApplyDensity(normalized);
            OnPropertyChanged();
            QueueSettingsSave();
        }
    }

    public bool OnlineArtworkEnabled
    {
        get => _settings.OnlineArtworkEnabled;
        set
        {
            if (_settings.OnlineArtworkEnabled == value) return;
            _settings.OnlineArtworkEnabled = value;
            OnPropertyChanged();
            QueueSettingsSave();
            RestartArtworkLookup();
        }
    }

    public bool CacheOnlineArtwork
    {
        get => _settings.CacheOnlineArtwork;
        set
        {
            if (_settings.CacheOnlineArtwork == value) return;
            _settings.CacheOnlineArtwork = value;
            OnPropertyChanged();
            QueueSettingsSave();
            RestartArtworkLookup();
        }
    }

    public string OnlineArtworkStatus => _tmdbArtworkService.ConfigurationStatus;

    public bool ResumePlayback
    {
        get => _settings.ResumePlayback;
        set => UpdatePlaybackSetting(_settings.ResumePlayback, value, v => _settings.ResumePlayback = v, nameof(ResumePlayback));
    }

    public int SeekStepSeconds
    {
        get => _settings.SeekStepSeconds;
        set
        {
            var normalized = SeekStepOptions.Contains(value) ? value : 10;
            if (_settings.SeekStepSeconds == normalized) return;
            _settings.SeekStepSeconds = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SeekBackToolTip));
            OnPropertyChanged(nameof(SeekForwardToolTip));
            QueueSettingsSave();
        }
    }

    public bool RememberVolume
    {
        get => _settings.RememberVolume;
        set
        {
            if (_settings.RememberVolume == value) return;
            _settings.RememberVolume = value;
            if (value) _settings.Volume = Volume;
            OnPropertyChanged();
            QueueSettingsSave();
        }
    }

    public bool DoubleClickFullscreen
    {
        get => _settings.DoubleClickFullscreen;
        set => UpdatePlaybackSetting(_settings.DoubleClickFullscreen, value, v => _settings.DoubleClickFullscreen = v, nameof(DoubleClickFullscreen));
    }

    public async Task InitializeViewAsync()
    {
        if (_initialized) return;
        _initialized = true;

        var loaded = await _settingsService.LoadAsync();
        CopySettings(loaded, _settings);
        _volume = _settings.RememberVolume ? _settings.Volume : 68;
        _selectedTheme = _settings.ThemeName;
        _selectedTypography = _settings.TypographyProfile;
        StudioAppearanceService.ApplyTheme(_selectedTheme);
        StudioAppearanceService.ApplyTypography(_selectedTypography, _settings.TextSize);
        StudioAppearanceService.ApplyDensity(_settings.InterfaceDensity);
        StudioAppearanceService.ApplyMotion(_settings.ReduceMotion);
        OnPropertyChanged(nameof(Volume));
        OnPropertyChanged(nameof(SelectedTheme));
        OnPropertyChanged(nameof(SelectedTypography));
        OnPropertyChanged(nameof(CinemaAutoExpand));
        OnPropertyChanged(nameof(RestoreSidebarWhenPlaybackStops));
        OnPropertyChanged(nameof(ShowSidebarWhenPaused));
        OnPropertyChanged(nameof(FullscreenHideTransport));
        OnPropertyChanged(nameof(FullscreenShowControlsOnMouseMove));
        OnPropertyChanged(nameof(FullscreenControlsAutoHideSeconds));
        OnPropertyChanged(nameof(FullscreenCursorAutoHide));
        OnPropertyChanged(nameof(EscapeExitsFullscreen));
        OnPropertyChanged(nameof(RememberWindowGeometry));
        OnPropertyChanged(nameof(ReduceMotion));
        OnPropertyChanged(nameof(InterfaceDensity));
        OnPropertyChanged(nameof(OnlineArtworkEnabled));
        OnPropertyChanged(nameof(CacheOnlineArtwork));
        OnPropertyChanged(nameof(OnlineArtworkStatus));
        OnPropertyChanged(nameof(ResumePlayback));
        OnPropertyChanged(nameof(SeekStepSeconds));
        OnPropertyChanged(nameof(SeekBackToolTip));
        OnPropertyChanged(nameof(SeekForwardToolTip));
        OnPropertyChanged(nameof(RememberVolume));
        OnPropertyChanged(nameof(DoubleClickFullscreen));
        UpdateCinemaSidebarState();

        StatusText = "Player shell ready. Waiting for the native video surface.";
        await ScanDiscsAsync();
        _discWatchTask = WatchOpticalDiscsAsync(_discWatchCancellation.Token);
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
        _artworkCancellation?.Cancel();
        _artworkCancellation?.Dispose();

        CancelBluRayTitleDiscovery();
        _discWatchCancellation.Cancel();
        if (_discWatchTask is not null)
        {
            try
            {
                await _discWatchTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await PersistResumeCheckpointAsync(force: true).ConfigureAwait(false);
        if (_settings.RememberVolume)
            _settings.Volume = Volume;
        await PersistSettingsAsync().ConfigureAwait(false);
        await _playback.DisposeAsync().ConfigureAwait(false);
        _bluRayDiscoveryCancellation?.Dispose();
        _discWatchCancellation.Dispose();
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
            _activeDisc = null;
            _settings.LastOpenDirectory = Path.GetDirectoryName(path);
            _activeResumePath = Path.GetFullPath(path);
            _lastResumePersistUtc = DateTimeOffset.MinValue;
            StatusText = $"Opening {Path.GetFileName(path)}...";
            await _playback.OpenFileAsync(path);
            StatusText = "Playback active. Reading tracks, subtitles, and chapters...";
            await RefreshMediaNavigationWithRetryAsync(10, 300);
            var resumedAt = await TryResumeFileAsync(_activeResumePath);
            StatusText = resumedAt is double seconds
                ? $"Playback resumed at {FormatTime(TimeSpan.FromSeconds(seconds))}. Tracks, subtitles, and chapters are ready."
                : "Playback active. Tracks, subtitles, and chapters are ready.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            await _logger.ErrorAsync($"Could not open media: {path}", ex);
        }
    }


    private async Task ToggleTracksAsync()
    {
        IsSettingsPanelOpen = false;
        IsTracksPanelOpen = !IsTracksPanelOpen;
        if (!IsTracksPanelOpen) return;

        // The native video host and libmpv can finish restoring asynchronously after
        // fullscreen. Refresh until the complete navigation snapshot is available.
        await Task.Delay(75);
        await RefreshMediaNavigationWithRetryAsync(8, 150);
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
        var generation = _mediaGeneration;
        var source = _playback.Snapshot.Source;
        if (_playback.Snapshot.State == PlaybackState.Loading)
        {
            await _logger.InfoAsync("Deferred media-navigation refresh while the selected title is still loading.");
            return false;
        }

        var tracks = await _playback.GetTracksAsync();
        var chapters = await _playback.GetChaptersAsync();
        // A slow read for the previous file/title must never populate the next one.
        if (_shutdown || generation != _mediaGeneration ||
            !string.Equals(source, _presentationSource, StringComparison.Ordinal) ||
            !string.Equals(source, _playback.Snapshot.Source, StringComparison.Ordinal) ||
            _playback.Snapshot.State is not (PlaybackState.Playing or PlaybackState.Paused)) return false;

        // During fullscreen restoration mpv may briefly expose an incomplete track-list.
        // Never replace a populated UI with that transient snapshot.
        var incomingAudioCount = tracks.Count(track => track.Kind == MediaTrackKind.Audio);
        var incomingSubtitleCount = tracks.Count(track => track.Kind == MediaTrackKind.Subtitle);
        var hasExistingNavigation = AudioTracks.Count > 0 || SubtitleTracks.Count > 1 || Chapters.Count > 0;
        if (hasExistingNavigation && incomingAudioCount == 0 && incomingSubtitleCount == 0 && chapters.Count == 0)
        {
            await _logger.InfoAsync("Ignored transient empty media-navigation snapshot.");
            return false;
        }

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

        _presentationTracks = tracks;
        _presentationChapters = chapters;
        MetadataStatus = tracks.Count > 0 || chapters.Count > 0 ? string.Empty : "Media details are not available yet.";
        UpdateMediaPresentation(_playback.Snapshot);

        await _logger.InfoAsync($"Media navigation refreshed: {AudioTracks.Count} audio, {Math.Max(0, SubtitleTracks.Count - 1)} subtitles, {Chapters.Count} chapters.");
        return tracks.Count > 0 || chapters.Count > 0;
    }

    private async Task<bool> RefreshMediaNavigationWithRetryAsync(int attempts = 120, int delayMilliseconds = 500)
    {
        var generation = _mediaGeneration;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (_shutdown || generation != _mediaGeneration) return false;
            if (_playback.Snapshot.State is PlaybackState.Faulted or PlaybackState.Stopped) return false;
            if (_playback.Snapshot.State == PlaybackState.Loading)
            {
                await Task.Delay(delayMilliseconds);
                continue;
            }

            if (await RefreshMediaNavigationAsync()) return true;
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
            CancelBluRayTitleDiscovery();
            _activeDisc = disc;
            _activeResumePath = null;
            StatusText = $"Opening {disc.KindLabel} from {disc.DriveRoot}...";
            IsTracksPanelOpen = false;
            IsBluRayPanelOpen = false;

            if (disc.Kind == DiscKind.BluRay)
            {
                if (!EnsureAacsConfigurationReady())
                    return;
                await _playback.OpenBluRayAsync(disc);
                StatusText = "Blu-ray main feature is loading...";
            }
            else if (disc.Kind == DiscKind.Dvd)
            {
                await _playback.OpenDvdAsync(disc);
                StatusText = "DVD longest title is loading...";
            }
            else
            {
                StatusText = "This optical-disc type is not yet playable.";
                return;
            }

            var navigationReady = await RefreshMediaNavigationWithRetryAsync();
            if (_playback.Snapshot.State is PlaybackState.Faulted or PlaybackState.Stopped)
            {
                StatusText = disc.Kind == DiscKind.BluRay
                    ? FormatBluRayFailure(_playback.LastPlaybackError)
                    : FormatDvdFailure(_playback.LastPlaybackError);
            }
            else
            {
                StatusText = navigationReady
                    ? $"{disc.KindLabel} playback active. Audio, subtitles, and chapters are ready in Tracks."
                    : $"{disc.KindLabel} playback active. Media details are still loading. Open Tracks & chapters to refresh if they do not appear.";
            }
        }
        catch (Exception ex)
        {
            if (disc.Kind == DiscKind.Dvd && ex.Message.Contains("isolated native probe failed", StringComparison.OrdinalIgnoreCase))
            {
                StatusText = ex.Message;
                MessageBox.Show(
                    ex.Message,
                    "DVD playback safety check failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else
            {
                StatusText = disc.Kind == DiscKind.BluRay
                    ? "Blu-ray could not be opened. The disc may require AACS keys or unsupported protection."
                    : "DVD could not be opened. Check libdvdcss availability and the playback log.";
            }
            await _logger.ErrorAsync($"Could not open {disc.KindLabel} from {disc.DriveRoot}", ex);
        }
    }

    private async Task BrowseDiscTitlesAsync(DiscSource? disc)
    {
        if (disc is null) return;
        IsSettingsPanelOpen = false;
        IsTracksPanelOpen = false;
        IsBluRayPanelOpen = true;

        var sameDisc = _activeDisc is not null &&
            string.Equals(GetDiscIdentity(_activeDisc), GetDiscIdentity(disc), StringComparison.Ordinal);

        _activeDisc = disc;

        // Reopening a completed browser is instant and never touches the physical drive.
        if (sameDisc && BluRayTitles.Count > 0)
        {
            StatusText = $"Using cached title metadata for {CleanDiscDisplayName(disc)}.";
            await RefreshBluRayTitlesAsync(cancellationToken: CancellationToken.None);
            return;
        }

        if (disc.Kind == DiscKind.BluRay && !EnsureAacsConfigurationReady())
            return;

        var discPlaybackActive = (_playback.Snapshot.Source?.StartsWith("Blu-ray disc", StringComparison.OrdinalIgnoreCase) == true ||
                                  _playback.Snapshot.Source?.StartsWith("DVD disc", StringComparison.OrdinalIgnoreCase) == true) &&
            (_playback.Snapshot.State is PlaybackState.Loading or PlaybackState.Playing or PlaybackState.Paused);

        if (discPlaybackActive)
        {
            StatusText = "Stopping disc playback before title discovery to prevent optical-drive contention...";
            await _playback.StopAsync();
            await Task.Delay(500);
        }

        CancelBluRayTitleDiscovery();
        var discoveryCancellation = new CancellationTokenSource();
        _bluRayDiscoveryCancellation = discoveryCancellation;

        try
        {
            IsBluRayTitleDiscoveryBusy = true;
            BluRayTitles.Clear();
            SelectedBluRayTitle = null;
            BluRayTitleSummary =
                $"Scanning {CleanDiscDisplayName(disc)} without starting playback. Parallax remains responsive, and you can cancel at any time.";
            StatusText = $"Discovering {disc.KindLabel} titles on {disc.DriveRoot}. Playback will begin only after you choose a title...";

            await RefreshBluRayTitlesAsync(cancellationToken: discoveryCancellation.Token);

            StatusText = BluRayTitles.Count == 0
                ? $"No {disc.KindLabel} title metadata was reported. Direct playback remains available."
                : $"Found {BluRayTitles.Count} {disc.KindLabel} title{(BluRayTitles.Count == 1 ? string.Empty : "s")}. Select one to begin playback.";
        }
        catch (OperationCanceledException) when (discoveryCancellation.IsCancellationRequested)
        {
            BluRayTitleSummary = "Title discovery was cancelled. Choose Browse titles to try again, or play the disc directly.";
            StatusText = $"{disc.KindLabel} title discovery cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"{disc.KindLabel} titles could not be read. Check the playback log for details.";
            BluRayTitleSummary = "Title discovery failed. The playback log contains the discovery details.";
            await _logger.ErrorAsync($"Could not browse {disc.KindLabel} titles from {disc.DriveRoot}", ex);
        }
        finally
        {
            if (ReferenceEquals(_bluRayDiscoveryCancellation, discoveryCancellation))
            {
                _bluRayDiscoveryCancellation = null;
                IsBluRayTitleDiscoveryBusy = false;
            }
            discoveryCancellation.Dispose();
        }
    }

    private async Task PlayBluRayTitleAsync(BluRayTitle? title)
    {
        title ??= SelectedBluRayTitle;
        if (title is null || _activeDisc is null) return;

        if (title.IsActive && (_playback.Snapshot.State is PlaybackState.Loading or PlaybackState.Playing or PlaybackState.Paused))
        {
            StatusText = $"{_activeDisc.KindLabel} title {title.DisplayNumber} is already playing.";
            return;
        }

        CancelBluRayTitleDiscovery();

        try
        {
            SelectedBluRayTitle = title;
            _activeResumePath = null;
            if (_activeDisc.Kind == DiscKind.Dvd)
            {
                _lastDvdReconciliationUiSource = string.Empty;
                _dvdReconciliationUiRefreshPending = false;
            }
            StatusText = $"Switching to {_activeDisc.KindLabel} title {title.DisplayNumber}...";
            BluRayTitleSummary = $"Loading Title {title.DisplayNumber} — {title.DurationText}. Track and chapter metadata will refresh automatically.";

            if (_activeDisc.Kind == DiscKind.BluRay)
                await _playback.OpenBluRayTitleAsync(_activeDisc, title.Index);
            else if (_activeDisc.Kind == DiscKind.Dvd)
                await _playback.OpenDvdTitleAsync(_activeDisc, title.Index);
            else
                throw new InvalidOperationException("This optical-disc type does not support direct title playback.");

            await RefreshMediaNavigationWithRetryAsync();
            await RefreshBluRayTitlesWithRetryAsync(title.Index); // uses the per-disc cache; no physical rescan
            var activeTitle = BluRayTitles.FirstOrDefault(item => item.IsActive);
            if (_activeDisc.Kind == DiscKind.Dvd && activeTitle is not null)
                SelectedBluRayTitle = activeTitle;

            StatusText = _playback.Snapshot.State is PlaybackState.Faulted or PlaybackState.Stopped
                ? _activeDisc.Kind == DiscKind.BluRay
                    ? FormatBluRayFailure(_playback.LastPlaybackError)
                    : FormatDvdFailure(_playback.LastPlaybackError)
                : _activeDisc.Kind == DiscKind.Dvd && activeTitle is not null && activeTitle.Index != title.Index
                    ? $"DVD playback matched the loaded program to title {activeTitle.DisplayNumber} at {activeTitle.DurationText} using runtime and chapter data."
                    : $"{_activeDisc.KindLabel} title {(activeTitle?.DisplayNumber ?? title.DisplayNumber)} is playing. Tracks, subtitles, chapters, duration, and timeline were refreshed.";
        }
        catch (Exception ex)
        {
            if (_activeDisc.Kind == DiscKind.Dvd && ex.Message.Contains("isolated native probe failed", StringComparison.OrdinalIgnoreCase))
            {
                StatusText = ex.Message;
                MessageBox.Show(
                    ex.Message,
                    "DVD playback safety check failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else
            {
                StatusText = $"{_activeDisc.KindLabel} title {title.DisplayNumber} could not be opened.";
            }
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

    private static string FormatDvdFailure(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return "DVD loading failed. Open the Parallax Studio log for details.";
        if (error.Contains("css", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("encrypted", StringComparison.OrdinalIgnoreCase))
            return "Protected DVD could not be opened. Verify that libdvdcss-2.dll is present in the app output.";
        if (error.Contains("region", StringComparison.OrdinalIgnoreCase))
            return "DVD loading failed because the drive or disc reported a region restriction.";
        return $"DVD loading failed: {error}";
    }

    private async Task RefreshBluRayTitlesAsync(
        int? preferredSelectionIndex = null,
        CancellationToken cancellationToken = default)
    {
        var disc = _activeDisc;
        if (disc is null)
        {
            BluRayTitles.Clear();
            SelectedBluRayTitle = null;
            BluRayTitleSummary = "No optical disc is selected.";
            return;
        }

        var selectionIndex = preferredSelectionIndex ?? SelectedBluRayTitle?.Index;
        var titles = disc.Kind == DiscKind.Dvd
            ? await _playback.GetDvdTitlesAsync(disc, cancellationToken)
            : await _playback.GetBluRayTitlesAsync(disc, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        // Apply one completed batch. Avoid hundreds of incremental WPF layout passes.
        BluRayTitles.Clear();
        foreach (var title in titles) BluRayTitles.Add(title);
        if (titles.Count == 0)
        {
            SelectedBluRayTitle = null;
            BluRayTitleSummary = "No title metadata was reported. Direct disc playback can still be used.";
            return;
        }

        var active = titles.FirstOrDefault(title => title.IsActive);
        SelectedBluRayTitle = disc.Kind == DiscKind.Dvd && active?.IsDvdMappingVerified == true
            ? active
            : titles.FirstOrDefault(title => title.Index == selectionIndex)
                ?? active
                ?? titles.FirstOrDefault(title => title.IsLikelyMainFeature)
                ?? titles[0];

        var main = titles.FirstOrDefault(title => title.IsLikelyMainFeature);
        var episodeCount = titles.Count(title => title.IsLikelyEpisode);
        BluRayTitleSummary = active is not null
            ? active.SourceKind == DiscKind.Dvd && active.DvdDisplayTitleNumber is int
                ? $"{titles.Count} titles discovered. DVD title {active.DvdDisplayTitleNumber} matched title {active.DisplayNumber} at {active.DurationText}."
                : $"{titles.Count} titles discovered. Title {active.DisplayNumber} is playing at {active.DurationText}."
            : episodeCount >= 2
                ? $"{titles.Count} titles discovered, including {episodeCount} episode-length candidates. Select any title to play it directly."
                : main is null
                    ? $"{titles.Count} titles discovered. Select any title to play it directly."
                    : $"{titles.Count} titles discovered. Title {main.DisplayNumber} is the strongest feature candidate at {main.DurationText}.";
    }

    private async Task RefreshBluRayTitlesWithRetryAsync(
        int? preferredSelectionIndex = null,
        CancellationToken cancellationToken = default)
    {
        const int maximumAttempts = 12;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            await RefreshBluRayTitlesAsync(preferredSelectionIndex, cancellationToken);

            var active = BluRayTitles.FirstOrDefault(title => title.IsActive);
            if (_activeDisc?.Kind == DiscKind.Dvd)
            {
                // The first refresh can still contain the provisional metadata title.
                // Wait for the playback service to reconcile the host runtime/chapter
                // data and expose a verified metadata-to-dvd://N mapping.
                if (active?.IsDvdMappingVerified == true)
                {
                    SelectedBluRayTitle = active;
                    return;
                }
            }
            else if (active is not null || (BluRayTitles.Count > 0 && attempt >= 3))
            {
                return;
            }

            await Task.Delay(300, cancellationToken);
        }

        // Keep playback usable even when an unusual DVD cannot be matched confidently.
        // The last refresh still presents the provisional title instead of hanging the UI.
    }


    private void ToggleBluRayPanel()
    {
        IsSettingsPanelOpen = false;
        if (IsBluRayPanelOpen && IsBluRayTitleDiscoveryBusy)
            CancelBluRayTitleDiscovery();
        IsBluRayPanelOpen = !IsBluRayPanelOpen;
    }

    private void CancelBluRayTitleDiscovery()
    {
        var cancellation = _bluRayDiscoveryCancellation;
        if (cancellation is null || cancellation.IsCancellationRequested) return;
        BluRayTitleSummary = "Cancelling title discovery and releasing the optical drive...";
        StatusText = "Cancelling optical-disc title discovery...";
        cancellation.Cancel();
    }

    private async Task ScanDiscsAsync()
    {
        StatusText = "Scanning optical drives...";
        await ScanDiscsCoreAsync(isAutomatic: false, CancellationToken.None);
    }

    private async Task WatchOpticalDiscsAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                // Avoid probing during libmpv's most intensive title-open phase. During
                // MakeMKV discovery the lightweight watcher remains active so an eject or
                // disc replacement can cancel the scan promptly.
                if (_playback.Snapshot.State == PlaybackState.Loading)
                    continue;

                await ScanDiscsCoreAsync(isAutomatic: true, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ScanDiscsCoreAsync(bool isAutomatic, CancellationToken cancellationToken)
    {
        await _discScanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var discs = await _discService.GetAvailableDiscsAsync(cancellationToken).ConfigureAwait(false);
            var snapshotKey = GetDiscSnapshotKey(discs);

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                await dispatcher.InvokeAsync(() => ApplyDiscSnapshot(discs, snapshotKey, isAutomatic));
            }
            else
            {
                ApplyDiscSnapshot(discs, snapshotKey, isAutomatic);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!isAutomatic) StatusText = "Optical-drive scan failed.";
            await _logger.ErrorAsync("Optical-drive scan failed.", ex, cancellationToken);
        }
        finally
        {
            _discScanGate.Release();
        }
    }

    private void ApplyDiscSnapshot(IReadOnlyList<DiscSource> discs, string snapshotKey, bool isAutomatic)
    {
        var previousSnapshotKey = _discSnapshotKey;
        var mediaChanged = !string.IsNullOrWhiteSpace(previousSnapshotKey) &&
            !string.Equals(previousSnapshotKey, snapshotKey, StringComparison.Ordinal);
        var previousActiveDisc = _activeDisc;

        Discs.Clear();
        foreach (var disc in discs) Discs.Add(disc);
        _discSnapshotKey = snapshotKey;

        if (mediaChanged && previousActiveDisc is not null)
        {
            var replacement = discs.FirstOrDefault(disc =>
                string.Equals(
                    Path.GetPathRoot(disc.DriveRoot) ?? disc.DriveRoot,
                    Path.GetPathRoot(previousActiveDisc.DriveRoot) ?? previousActiveDisc.DriveRoot,
                    StringComparison.OrdinalIgnoreCase));

            if (replacement is null ||
                !string.Equals(GetDiscIdentity(previousActiveDisc), GetDiscIdentity(replacement), StringComparison.Ordinal))
            {
                CancelBluRayTitleDiscovery();
                _activeDisc = null;
                BluRayTitles.Clear();
                SelectedBluRayTitle = null;
                IsBluRayPanelOpen = false;
                BluRayTitleSummary = "The previous disc was removed. Insert a Blu-ray or DVD and choose Browse titles.";
            }
        }

        var readyDiscs = discs.Where(disc => disc.IsReady).ToArray();
        if (mediaChanged)
        {
            StatusText = readyDiscs.Length == 0
                ? "Optical media removed. Waiting for a disc..."
                : readyDiscs.Length == 1
                    ? $"Detected {CleanDiscDisplayName(readyDiscs[0])}. Ready to play or browse titles."
                    : $"Detected {readyDiscs.Length} ready optical discs.";
            return;
        }

        if (!isAutomatic)
        {
            StatusText = discs.Count == 0
                ? "Scan complete. No optical drives detected. File playback remains available."
                : readyDiscs.Length == 0
                    ? $"Scan complete. Found {discs.Count} optical drive{(discs.Count == 1 ? string.Empty : "s")}, but no disc is currently ready."
                    : readyDiscs.Length == 1
                        ? $"Scan complete. {CleanDiscDisplayName(readyDiscs[0])} — {readyDiscs[0].KindLabel} on {Path.GetPathRoot(readyDiscs[0].DriveRoot) ?? readyDiscs[0].DriveRoot}. Ready to play or browse titles."
                        : $"Scan complete. {readyDiscs.Length} discs are ready across {discs.Count} optical drive{(discs.Count == 1 ? string.Empty : "s")}.";
        }
    }

    private static string GetDiscSnapshotKey(IEnumerable<DiscSource> discs) =>
        string.Join("||", discs
            .OrderBy(disc => disc.DriveRoot, StringComparer.OrdinalIgnoreCase)
            .Select(GetDiscIdentity));

    private static string GetDiscIdentity(DiscSource disc) =>
        $"{Path.GetPathRoot(disc.DriveRoot) ?? disc.DriveRoot}|{disc.DisplayName}|{disc.Kind}|{disc.IsReady}"
            .ToUpperInvariant();

    private static string CleanDiscDisplayName(DiscSource disc)
    {
        var root = Path.GetPathRoot(disc.DriveRoot) ?? disc.DriveRoot;
        var value = disc.DisplayName;
        if (value.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            value = value[root.Length..].Trim();
        return string.IsNullOrWhiteSpace(value) ? disc.KindLabel : value.Replace('_', ' ');
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
            await PersistResumeCheckpointAsync(force: true);
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

    private void ToggleSettingsPanel()
    {
        IsSettingsPanelOpen = !IsSettingsPanelOpen;
        if (IsSettingsPanelOpen)
        {
            IsTracksPanelOpen = false;
            if (IsBluRayPanelOpen && IsBluRayTitleDiscoveryBusy) CancelBluRayTitleDiscovery();
            IsBluRayPanelOpen = false;
        }
    }

    private void ToggleSidebarVisibility()
    {
        var nextHidden = !IsCinemaSidebarHidden;
        _sidebarHiddenOverride = nextHidden;
        IsCinemaSidebarHidden = nextHidden;
    }

    private void ToggleFullscreen()
    {
        IsSettingsPanelOpen = false;
        IsTracksPanelOpen = false;
        if (IsBluRayPanelOpen && IsBluRayTitleDiscoveryBusy) CancelBluRayTitleDiscovery();
        IsBluRayPanelOpen = false;
        IsFullscreen = !IsFullscreen;
        ToggleFullscreenRequested?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateCinemaSidebarState()
    {
        if (_sidebarHiddenOverride is bool overrideHidden)
        {
            IsCinemaSidebarHidden = overrideHidden;
            return;
        }

        if (!_settings.CinemaAutoExpand)
        {
            IsCinemaSidebarHidden = false;
            return;
        }

        if ((_currentPlaybackState is PlaybackState.Stopped or PlaybackState.Idle or PlaybackState.Faulted) &&
            !_settings.RestoreSidebarWhenPlaybackStops)
        {
            return;
        }

        IsCinemaSidebarHidden = _currentPlaybackState switch
        {
            PlaybackState.Playing => true,
            PlaybackState.Paused => !_settings.ShowSidebarWhenPaused,
            _ => false
        };
    }

    private void UpdateCinemaSetting(bool current, bool value, Action<bool> assign, string propertyName, bool refreshSidebar = false)
    {
        if (current == value) return;
        assign(value);
        OnPropertyChanged(propertyName);
        if (refreshSidebar)
        {
            _sidebarHiddenOverride = null;
            UpdateCinemaSidebarState();
        }
        CinemaSettingsChanged?.Invoke(this, EventArgs.Empty);
        QueueSettingsSave();
    }

    private void UpdatePlaybackSetting(bool current, bool value, Action<bool> assign, string propertyName)
    {
        if (current == value) return;
        assign(value);
        OnPropertyChanged(propertyName);
        QueueSettingsSave();
    }

    private void QueueSettingsSave() => _ = PersistSettingsAsync();

    private async Task PersistSettingsAsync()
    {
        await _settingsSaveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _settingsService.SaveAsync(_settings).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _logger.WarningAsync($"Could not persist settings: {ex.Message}").ConfigureAwait(false);
        }
        finally
        {
            _settingsSaveGate.Release();
        }
    }

    private void OnPlaybackStateChanged(object? sender, PlaybackStateChangedEventArgs e)
    {
        void Apply()
        {
            var snapshot = e.Snapshot;
            if (!string.Equals(snapshot.Source, _playback.Snapshot.Source, StringComparison.Ordinal)) return;
            if (snapshot.State is PlaybackState.Loading or PlaybackState.Playing or PlaybackState.Paused &&
                (!string.Equals(_presentationSource, snapshot.Source, StringComparison.Ordinal) ||
                 (snapshot.State == PlaybackState.Loading && _currentPlaybackState != PlaybackState.Loading)))
                ResetMediaPresentation(snapshot.Source);
            HasActiveMedia = snapshot.State is PlaybackState.Loading or PlaybackState.Playing or PlaybackState.Paused;
            NowPlayingTitle = HasActiveMedia ? snapshot.Title : "Nothing playing";
            SourceText = HasActiveMedia ? MediaPresentation.SourceSummary(snapshot.Source) : "Open a video to begin";
            var artworkSourceIsOptical = MediaPresentation.SourceKind(snapshot.Source) is "Blu-ray" or "DVD";
            var artworkIdentityChanged =
                _artworkLookupGeneration != _mediaGeneration ||
                !string.Equals(
                    _artworkLookupTitle,
                    snapshot.Title,
                    StringComparison.Ordinal) ||
                (artworkSourceIsOptical &&
                 _artworkLookupDuration <= TimeSpan.Zero &&
                 snapshot.Duration > TimeSpan.Zero);

            if (HasActiveMedia && artworkIdentityChanged)
            {
                // Optical metadata can refine the provisional disc label after load.
                // Restart artwork once when the authoritative title changes.
                if (_artworkLookupGeneration == _mediaGeneration)
                {
                    _artworkCancellation?.Cancel();
                    _artworkCancellation?.Dispose();
                    _artworkCancellation = new CancellationTokenSource();
                    Artwork = null;
                }

                _artworkCancellation ??= new CancellationTokenSource();
                _artworkLookupGeneration = _mediaGeneration;
                _artworkLookupTitle = snapshot.Title;
                _artworkLookupDuration = snapshot.Duration;
                var artworkRequestId = ++_artworkRequestId;

                _ = LoadArtworkAsync(
                    snapshot.Source,
                    snapshot.Title,
                    snapshot.Duration,
                    _mediaGeneration,
                    artworkRequestId,
                    _artworkCancellation.Token);
            }
            if (!HasActiveMedia)
            {
                if (MediaDetails.Count > 0 || Artwork is not null || _presentationTracks.Count > 0)
                    ResetMediaPresentation(null);
                MetadataStatus = "Open media to see its details.";
                MediaDetailsText = MetadataStatus;
            }
            PositionText = FormatTime(snapshot.Position);
            DurationText = FormatTime(snapshot.Duration);
            PositionSeconds = snapshot.Position.TotalSeconds;
            DurationSeconds = snapshot.Duration.TotalSeconds;
            if (!IsScrubbing) ScrubPosition = snapshot.Position.TotalSeconds;
            IsMuted = snapshot.IsMuted;
            PlayPauseText = snapshot.State == PlaybackState.Paused ? "Play" : "Pause";
            UpdateMediaPresentation(snapshot);

            // Refresh on the first usable snapshot, even if the title browser is closed.
            // This only reads the existing playback state; it never starts a disc scan.
            if (HasActiveMedia && snapshot.State is PlaybackState.Playing or PlaybackState.Paused &&
                _automaticRefreshGeneration != _mediaGeneration)
            {
                _automaticRefreshGeneration = _mediaGeneration;
                _ = RefreshPresentationMetadataAsync(_mediaGeneration);
            }

            var previousState = _currentPlaybackState;
            _currentPlaybackState = snapshot.State;
            if (snapshot.State is PlaybackState.Stopped or PlaybackState.Idle or PlaybackState.Faulted)
            {
                if (_settings.RestoreSidebarWhenPlaybackStops)
                    _sidebarHiddenOverride = null;
            }
            else if ((previousState is PlaybackState.Stopped or PlaybackState.Idle or PlaybackState.Faulted) && snapshot.State == PlaybackState.Playing)
            {
                _sidebarHiddenOverride = null;
            }
            UpdateCinemaSidebarState();

            if (_activeResumePath is not null &&
                snapshot.State is PlaybackState.Playing or PlaybackState.Paused)
            {
                _ = PersistResumeCheckpointAsync(force: snapshot.State == PlaybackState.Paused);
            }
            else if (_activeResumePath is not null &&
                     _settings.ResumePlayback &&
                     snapshot.State == PlaybackState.Stopped &&
                     snapshot.Duration > TimeSpan.Zero &&
                     snapshot.Position >= snapshot.Duration - TimeSpan.FromSeconds(_settings.ResumeCompletionThresholdSeconds))
            {
                _ = _resumeStore.RemoveAsync(_activeResumePath);
            }

            // DVD reconciliation happens on the isolated playback polling thread just
            // before the reconciled snapshot is published. Use that state event as the
            // authoritative signal to refresh the title browser, rather than relying on
            // a short timing-based retry window during initial loading.
            if (_activeDisc?.Kind == DiscKind.Dvd &&
                snapshot.Source?.StartsWith("DVD disc", StringComparison.OrdinalIgnoreCase) == true &&
                IsBluRayPanelOpen &&
                snapshot.State is PlaybackState.Playing or PlaybackState.Paused &&
                snapshot.Duration > TimeSpan.Zero &&
                !BluRayTitles.Any(title => title.IsActive && title.IsDvdMappingVerified) &&
                !_dvdReconciliationUiRefreshPending &&
                !string.Equals(_lastDvdReconciliationUiSource, snapshot.Source, StringComparison.Ordinal))
            {
                _lastDvdReconciliationUiSource = snapshot.Source ?? string.Empty;
                _dvdReconciliationUiRefreshPending = true;
                _ = RefreshVerifiedDvdTitleUiAsync();
            }
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


    private async Task RefreshVerifiedDvdTitleUiAsync()
    {
        try
        {
            // Let the current dispatcher pass finish before replacing the observable
            // title collection. The playback service has already committed the mapping.
            await Task.Delay(125);
            if (_shutdown || _activeDisc?.Kind != DiscKind.Dvd || !IsBluRayPanelOpen) return;

            var preferredIndex = SelectedBluRayTitle?.Index;
            await RefreshBluRayTitlesAsync(preferredIndex, CancellationToken.None);

            var active = BluRayTitles.FirstOrDefault(title => title.IsActive && title.IsDvdMappingVerified);
            if (active is null) return;

            SelectedBluRayTitle = active;
            BluRayTitleSummary = $"{BluRayTitles.Count} titles discovered. DVD title {active.DvdDisplayTitleNumber} matched title {active.DisplayNumber} at {active.DurationText}.";
            StatusText = $"DVD playback matched the loaded program to title {active.DisplayNumber} at {active.DurationText} using runtime and chapter data.";
            await _logger.InfoAsync($"DVD reconciliation UI applied: DVD title {active.DvdDisplayTitleNumber}, metadata title {active.DisplayNumber}, runtime {active.DurationText}.");
        }
        catch (Exception ex)
        {
            await _logger.WarningAsync($"DVD reconciliation UI refresh failed: {ex.Message}");
        }
        finally
        {
            _dvdReconciliationUiRefreshPending = false;
        }
    }

    private async Task<double?> TryResumeFileAsync(string path)
    {
        if (!_settings.ResumePlayback) return null;

        var entry = await _resumeStore.GetAsync(path).ConfigureAwait(false);
        if (entry is null || entry.PositionSeconds < _settings.ResumeMinimumSeconds)
            return null;

        var runtimeDuration = _playback.Snapshot.Duration.TotalSeconds;
        var duration = runtimeDuration > 0 ? runtimeDuration : entry.DurationSeconds;
        if (duration > 0 &&
            duration - entry.PositionSeconds <= _settings.ResumeCompletionThresholdSeconds)
        {
            await _resumeStore.RemoveAsync(path).ConfigureAwait(false);
            return null;
        }

        await _playback.SeekAbsoluteAsync(entry.PositionSeconds).ConfigureAwait(false);
        return entry.PositionSeconds;
    }

    private async Task PersistResumeCheckpointAsync(bool force = false)
    {
        var path = _activeResumePath;
        if (!_settings.ResumePlayback || string.IsNullOrWhiteSpace(path)) return;

        var snapshot = _playback.Snapshot;
        var position = snapshot.Position.TotalSeconds;
        var duration = snapshot.Duration.TotalSeconds;
        if (duration <= 0 || position < _settings.ResumeMinimumSeconds) return;

        if (duration - position <= _settings.ResumeCompletionThresholdSeconds)
        {
            await _resumeStore.RemoveAsync(path).ConfigureAwait(false);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (!force && now - _lastResumePersistUtc < TimeSpan.FromSeconds(10)) return;
        _lastResumePersistUtc = now;

        await _resumeStore.UpsertAsync(
            new ResumeEntry(path, position, duration, now)).ConfigureAwait(false);
    }

    private void ResetMediaPresentation(string? source)
    {
        _mediaGeneration++;
        _presentationSource = source;
        _presentationTracks = [];
        _presentationChapters = [];
        MediaDetails.Clear();
        MediaBadges.Clear();
        MetadataStatus = "Reading media details…";
        MediaDetailsText = MetadataStatus;
        _isRefreshingMediaNavigation = true;
        try
        {
            AudioTracks.Clear();
            SubtitleTracks.Clear();
            Chapters.Clear();
        }
        finally { _isRefreshingMediaNavigation = false; }
        OnPropertyChanged(nameof(SourceLocation));
        _artworkCancellation?.Cancel();
        _artworkCancellation?.Dispose();
        _artworkCancellation = new CancellationTokenSource();
        _artworkLookupGeneration = -1;
        _artworkLookupTitle = null;
        _artworkLookupDuration = TimeSpan.Zero;
        _artworkRequestId++;
        Artwork = null;
    }

    private void RestartArtworkLookup()
    {
        if (!HasActiveMedia) return;

        _artworkCancellation?.Cancel();
        _artworkCancellation?.Dispose();
        _artworkCancellation = new CancellationTokenSource();
        Artwork = null;

        var snapshot = _playback.Snapshot;
        _artworkLookupGeneration = _mediaGeneration;
        _artworkLookupTitle = snapshot.Title;
        _artworkLookupDuration = snapshot.Duration;
        var artworkRequestId = ++_artworkRequestId;
        _ = LoadArtworkAsync(
            snapshot.Source,
            snapshot.Title,
            snapshot.Duration,
            _mediaGeneration,
            artworkRequestId,
            _artworkCancellation.Token);
    }

    private async Task LoadArtworkAsync(
        string? source,
        string? title,
        TimeSpan playbackDuration,
        int generation,
        long artworkRequestId,
        CancellationToken cancellationToken)
    {
        try
        {
            ImageSource? artwork = null;

            // Never crawl a physical optical disc for art. Local files keep the v3
            // same-basename/poster/folder/cover priority before any network lookup.
            if (MediaPresentation.SourceKind(source) == "Local file")
                artwork = await _artworkService.LoadAsync(source, cancellationToken);

            if (artwork is null)
            {
                var sourceKind = MediaPresentation.SourceKind(source);
                TimeSpan? tmdbDuration =
                    (sourceKind is "Blu-ray" or "DVD") && playbackDuration > TimeSpan.Zero
                        ? playbackDuration
                        : null;

                artwork = await _tmdbArtworkService.LoadAsync(
                    source,
                    title,
                    _settings.OnlineArtworkEnabled,
                    _settings.CacheOnlineArtwork,
                    cancellationToken,
                    tmdbDuration);
            }

            if (!_shutdown &&
                !cancellationToken.IsCancellationRequested &&
                generation == _mediaGeneration &&
                artworkRequestId == _artworkRequestId &&
                string.Equals(source, _playback.Snapshot.Source, StringComparison.Ordinal))
            {
                Artwork = artwork;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { await _logger.WarningAsync($"Artwork could not be loaded: {ex.Message}"); }
    }

    private void UpdateMediaPresentation(PlaybackSnapshot snapshot)
    {
        NowPlayingStateText = snapshot.State switch
        {
            PlaybackState.Loading => "LOADING",
            PlaybackState.Playing => "PLAYING",
            PlaybackState.Paused => "PAUSED",
            PlaybackState.Faulted => "UNAVAILABLE",
            _ => "READY"
        };
        var knownRuntime = HasActiveMedia && snapshot.Duration > TimeSpan.Zero;
        NowPlayingProgress = knownRuntime ? Math.Clamp(snapshot.Position.TotalSeconds / snapshot.Duration.TotalSeconds * 100, 0, 100) : 0;
        ProgressText = !HasActiveMedia ? "Ready for your next screening" : knownRuntime
            ? $"{FormatTime(snapshot.Position)} / {FormatTime(snapshot.Duration)}" : "Reading runtime…";
        RemainingText = knownRuntime ? $"{FormatTime(snapshot.Duration > snapshot.Position ? snapshot.Duration - snapshot.Position : TimeSpan.Zero)} left" : string.Empty;
        if (!HasActiveMedia) return;
        ReplaceIfChanged(MediaBadges, MediaPresentation.Badges(snapshot.Source, _presentationTracks));
        ReplaceIfChanged(MediaDetails, MediaPresentation.Details(snapshot, _presentationTracks, _presentationChapters));
        MediaDetailsText = string.Join(Environment.NewLine, MediaDetails.Select(row => $"{row.Label}: {row.Value}"));
    }

    private static void ReplaceIfChanged<T>(ObservableCollection<T> target, IReadOnlyList<T> values)
    {
        if (target.SequenceEqual(values)) return;
        target.Clear();
        foreach (var value in values) target.Add(value);
    }

    private async Task RefreshPresentationMetadataAsync(int generation)
    {
        try
        {
            // A short delay lets decoder properties settle after file-loaded.
            // Keep polling existing state briefly: tracks can precede video-params.
            for (var attempt = 0; attempt < 8; attempt++)
            {
                await Task.Delay(attempt == 0 ? 150 : 750);
                if (_shutdown || generation != _mediaGeneration ||
                    _playback.Snapshot.State is not (PlaybackState.Playing or PlaybackState.Paused)) return;
                await RefreshMediaNavigationAsync();
            }
            if (generation == _mediaGeneration && _presentationTracks.Count == 0 && _presentationChapters.Count == 0)
                MetadataStatus = "No additional media details were reported.";
        }
        catch (Exception ex)
        {
            if (generation == _mediaGeneration) MetadataStatus = "Media details are temporarily unavailable.";
            await _logger.WarningAsync($"Presentation metadata refresh failed: {ex.Message}");
        }
    }

    private static string FormatTime(TimeSpan value) => MediaPresentation.Time(value);

    private static void CopySettings(AppSettings source, AppSettings destination)
    {
        destination.SchemaVersion = Math.Max(6, source.SchemaVersion);
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
        destination.InterfaceDensity = source.InterfaceDensity == "Compact" ? "Compact" : "Comfortable";
        destination.OnlineArtworkEnabled = source.OnlineArtworkEnabled;
        destination.CacheOnlineArtwork = source.CacheOnlineArtwork;
        destination.CinemaAutoExpand = source.CinemaAutoExpand;
        destination.RestoreSidebarWhenPlaybackStops = source.RestoreSidebarWhenPlaybackStops;
        destination.ShowSidebarWhenPaused = source.ShowSidebarWhenPaused;
        destination.ResumePlayback = source.ResumePlayback;
        destination.ResumeMinimumSeconds = Math.Clamp(source.ResumeMinimumSeconds, 5, 300);
        destination.ResumeCompletionThresholdSeconds = Math.Clamp(source.ResumeCompletionThresholdSeconds, 15, 600);
        destination.SeekStepSeconds = source.SeekStepSeconds is 5 or 10 or 15 or 30 ? source.SeekStepSeconds : 10;
        destination.RememberVolume = source.RememberVolume;
        destination.DoubleClickFullscreen = source.DoubleClickFullscreen;
        destination.FullscreenHideTransport = source.FullscreenHideTransport;
        destination.FullscreenShowControlsOnMouseMove = source.FullscreenShowControlsOnMouseMove;
        destination.FullscreenControlsAutoHideSeconds = Math.Clamp(source.FullscreenControlsAutoHideSeconds, 1, 15);
        destination.FullscreenCursorAutoHide = source.FullscreenCursorAutoHide;
        destination.EscapeExitsFullscreen = source.EscapeExitsFullscreen;
        destination.RememberWindowGeometry = source.RememberWindowGeometry;
    }
}
