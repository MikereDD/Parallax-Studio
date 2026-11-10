using System.Globalization;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ParallaxStudio.Core.Enums;
using ParallaxStudio.Core.Events;
using ParallaxStudio.Core.Models;
using ParallaxStudio.Core.Services;

namespace ParallaxStudio.Infrastructure.Playback;

public sealed class MpvPlaybackService(IAppLogger logger) : IPlaybackService
{
    private string? _activeBluRayDevice;
    private int _activeBluRayTitleIndex = -1;
    private string? _loadedBluRayCacheKey;
    private readonly Dictionary<string, IReadOnlyList<BluRayTitle>> _bluRayTitleCache = new(StringComparer.OrdinalIgnoreCase);
    // libbluray edition list per disc (session-scoped), probed headlessly the first time
    // an indexed Blu-ray title is requested; the basis for evidence-based title mapping.
    private readonly Dictionary<string, IReadOnlyList<BluRayEditionInfo>> _bluRayEditionCache = new(StringComparer.OrdinalIgnoreCase);
    // Post-load verification state for indexed Blu-ray playback (refusal-over-guess).
    private BluRayTitle? _requestedBluRayTitle;
    private int _activeBluRayEditionSelector = -1;
    private bool _bluRayVerificationPending;
    private string? _bluRayVerificationSource;
    private string? _activeDvdDevice;
    private int _activeDvdTitleIndex = -1;
    private string? _loadedDvdCacheKey;
    private readonly Dictionary<string, IReadOnlyList<BluRayTitle>> _dvdTitleCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dvdFullMapCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<int, int>> _dvdPlaybackTitleByMetadataIndex = new(StringComparer.OrdinalIgnoreCase);
    // Per-disc outcome of the last full title map (unique, ambiguous, unmatched per title),
    // kept so a refused title can say why. Guarded by _dvdMappingSync.
    private readonly Dictionary<string, DvdTitleMapResult> _dvdTitleMapResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _dvdMappingSync = new();
    // Optical identities logged once per session; insufficient identities are made unique
    // per establishment so stale caches can never be reused across an indistinguishable
    // media transition.
    private readonly HashSet<string> _establishedOpticalIdentities = new(StringComparer.OrdinalIgnoreCase);
    private int _insufficientIdentitySequence;
    private DvdPlaybackHostClient? _dvdHost;
    private bool _usingDvdHost;
    // Ownership of DVD host routing. Every claim of (or release from) the DVD host path
    // advances the generation under _dvdHostSync; the poll loop may fault playback only
    // for the generation and host session it observed, so a superseded host's failure
    // can never disown a newly started one.
    private readonly object _dvdHostSync = new();
    private long _dvdHostGeneration;
    // Host session ids are monotonic per client. A generation claimed for a new start only
    // accepts sessions created after the claim, so the session being replaced cannot
    // publish state into, or fault, the new generation while the replacement is pending.
    private long _minimumDvdSessionId;
    private Func<IAppLogger, DvdPlaybackHostClient> _dvdHostFactory = static log => new DvdPlaybackHostClient(log);
    private bool _dvdHostReconciliationPending;
    private int _requestedDvdMetadataIndex = -1;
    private int _activeDvdPlaybackTitleNumber = -1;
    private string _activeDvdDisplayName = "DVD";
    private nint _videoWindowHandle;

    private readonly LibMpvLibrary _library = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _discDiscoveryGate = new(1, 1);
    private readonly CancellationTokenSource _pollCancellation = new();
    private nint _context;
    private Task? _pollTask;
    private bool _disposed;
    private bool _paused;
    private string? _discDecryptionProvider;

    /// <summary>Test seam: supplies the DVD host client (for example one pointing at a stub host).</summary>
    internal MpvPlaybackService(IAppLogger logger, Func<IAppLogger, DvdPlaybackHostClient> dvdHostFactory)
        : this(logger)
    {
        _dvdHostFactory = dvdHostFactory ?? throw new ArgumentNullException(nameof(dvdHostFactory));
    }

    public event EventHandler<PlaybackStateChangedEventArgs>? StateChanged;

    public PlaybackSnapshot Snapshot { get; private set; } = PlaybackSnapshot.Disconnected();
    public bool IsInitialized => _context != nint.Zero;
    public string BackendDescription { get; private set; } = "libmpv not checked";
    public string? LastPlaybackError { get; private set; }

    public async Task<PlaybackBackendStatus> ProbeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_library.TryLoad(out var error))
            {
                BackendDescription = "libmpv missing";
                return new PlaybackBackendStatus(false, BackendDescription, Error: error);
            }

            BackendDescription = _library.GetClientApiVersion();
            return new PlaybackBackendStatus(true, BackendDescription, _library.LoadedPath);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task InitializeAsync(nint windowHandle, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (windowHandle == nint.Zero) throw new ArgumentException("A valid video window handle is required.", nameof(windowHandle));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _videoWindowHandle = windowHandle;
            _dvdHost ??= _dvdHostFactory(logger);
            if (IsInitialized) return;
            ConfigureDiscDecryptionProvider();
            if (!_library.TryLoad(out var loadError))
            {
                throw new FileNotFoundException(loadError, "libmpv-2.dll");
            }

            _context = _library.Create();
            try
            {
                _library.SetOption(_context, "wid", windowHandle.ToInt64().ToString(CultureInfo.InvariantCulture));
                _library.SetOption(_context, "terminal", "no");
                _library.SetOption(_context, "osc", "no");
                _library.SetOption(_context, "idle", "yes");
                _library.SetOption(_context, "keep-open", "yes");
                _library.SetOption(_context, "force-window", "no");
                _library.SetOption(_context, "input-default-bindings", "no");
                _library.SetOption(_context, "input-vo-keyboard", "no");
                _library.Initialize(_context);
                _library.RequestLogMessages(_context, "info");
                _library.SetProperty(_context, "volume", Snapshot.Volume.ToString("0.##", CultureInfo.InvariantCulture));

                BackendDescription = _library.GetClientApiVersion();
                SetSnapshot(Snapshot with { State = PlaybackState.Idle });
                _pollTask = PollPlaybackAsync(_pollCancellation.Token);
                await logger.InfoAsync($"Initialized {BackendDescription} from {_library.LoadedPath}.", cancellationToken)
                    .ConfigureAwait(false);
                await logger.InfoAsync($"Disc decryption provider: {_discDecryptionProvider ?? "plain libaacs/libbdplus fallback"}.", cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                _library.TerminateDestroy(_context);
                _context = nint.Zero;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task OpenFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A media path is required.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Media file not found.", fullPath);

        await StopDvdHostIfActiveAsync(cancellationToken).ConfigureAwait(false);
        await WithInitializedContextAsync(context =>
        {
            SetSnapshot(Snapshot with
            {
                State = PlaybackState.Loading,
                Source = fullPath,
                Title = Path.GetFileNameWithoutExtension(fullPath),
                Position = TimeSpan.Zero,
                Duration = TimeSpan.Zero
            });

            _library.Command(context, "loadfile", fullPath, "replace");
            _paused = false;
        }, cancellationToken).ConfigureAwait(false);

        await logger.InfoAsync($"Opened media: {fullPath}", cancellationToken).ConfigureAwait(false);
    }

    public async Task OpenBluRayAsync(DiscSource disc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disc);
        if (disc.Kind != DiscKind.BluRay)
            throw new InvalidOperationException("The selected optical source is not a Blu-ray disc.");
        if (!disc.IsReady)
            throw new InvalidOperationException("The Blu-ray drive is not ready.");

        await StopDvdHostIfActiveAsync(cancellationToken).ConfigureAwait(false);
        await _discDiscoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var device = Path.GetPathRoot(disc.DriveRoot) ?? disc.DriveRoot;
            _activeBluRayDevice = device;
            _loadedBluRayCacheKey = EstablishDiscIdentity(disc);
            _activeBluRayTitleIndex = _bluRayTitleCache.TryGetValue(_loadedBluRayCacheKey, out var cachedBluRayTitles) && cachedBluRayTitles.Count > 0
                ? cachedBluRayTitles.OrderByDescending(title => title.Duration).First().Index
                : -1;
            LastPlaybackError = null;
            await logger.InfoAsync($"Blu-ray request: source=bd://longest, device={device}, label={disc.DisplayName}.", cancellationToken).ConfigureAwait(false);
            await LogNativeDiscLibrariesAsync(cancellationToken).ConfigureAwait(false);
            await WithInitializedContextAsync(context =>
            {
                SetSnapshot(Snapshot with
                {
                    State = PlaybackState.Loading,
                    Source = $"Blu-ray disc • {device}",
                    Title = CleanDiscTitle(disc.DisplayName, device),
                    Position = TimeSpan.Zero,
                    Duration = TimeSpan.Zero
                });

                _library.SetProperty(context, "bluray-device", device);
                _library.Command(context, "loadfile", "bd://longest", "replace");
                _paused = false;
            }, cancellationToken).ConfigureAwait(false);

            await logger.InfoAsync($"Submitted Blu-ray main-feature request for {device}.", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _discDiscoveryGate.Release();
        }
    }

    public async Task OpenBluRayTitleAsync(DiscSource disc, int titleIndex, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disc);
        if (disc.Kind != DiscKind.BluRay)
            throw new InvalidOperationException("The selected optical source is not a Blu-ray disc.");
        if (!disc.IsReady)
            throw new InvalidOperationException("The Blu-ray drive is not ready.");
        if (titleIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(titleIndex));

        await StopDvdHostIfActiveAsync(cancellationToken).ConfigureAwait(false);
        await _discDiscoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var device = Path.GetPathRoot(disc.DriveRoot) ?? disc.DriveRoot;
            var cacheKey = EstablishDiscIdentity(disc);

            if (!_bluRayTitleCache.TryGetValue(cacheKey, out var titles))
            {
                titles = await DiscoverTitlesWithMakeMkvAsync(disc, cancellationToken).ConfigureAwait(false);
                if (titles.Count > 0)
                    _bluRayTitleCache[cacheKey] = titles;
            }

            var requested = titles.FirstOrDefault(title => title.Index == titleIndex)
                ?? throw new InvalidOperationException($"Blu-ray title {titleIndex + 1} is not present in the current disc metadata.");

            var editions = await EnsureBluRayEditionMapAsync(disc, cacheKey, cancellationToken).ConfigureAwait(false);
            var resolution = BluRayEditionResolver.Resolve(requested, editions);
            if (!resolution.IsResolved)
            {
                await logger.InfoAsync(
                    $"Blu-ray title {titleIndex + 1} refused for {device}: {resolution.Detail}.",
                    cancellationToken).ConfigureAwait(false);
                throw new OpticalTitleUnavailableException(
                    $"Parallax could not confidently match Blu-ray title {titleIndex + 1} " +
                    $"({DescribeBluRayTitle(requested)}) to a libbluray edition: {resolution.Detail}. " +
                    "The title was not started rather than risking playback of the wrong program.");
            }

            _activeBluRayDevice = device;
            _loadedBluRayCacheKey = cacheKey;
            _activeBluRayTitleIndex = titleIndex;
            _requestedBluRayTitle = requested;
            _activeBluRayEditionSelector = resolution.Selector;
            _bluRayVerificationPending = true;
            _bluRayVerificationSource = $"Blu-ray disc • {device} • title {titleIndex + 1}";
            LastPlaybackError = null;

            await logger.InfoAsync(
                $"Blu-ray title resolved: MakeMKV title {requested.DisplayNumber} ({DescribeBluRayTitle(requested)}) -> " +
                $"libbluray edition {resolution.Selector} (title {resolution.Selector + 1}) by {resolution.Detail}; playing bd://{resolution.Selector}.",
                cancellationToken).ConfigureAwait(false);

            await WithInitializedContextAsync(context =>
            {
                SetSnapshot(Snapshot with
                {
                    State = PlaybackState.Loading,
                    Source = _bluRayVerificationSource,
                    Title = $"{CleanDiscTitle(disc.DisplayName, device)} — Title {titleIndex + 1}",
                    Position = TimeSpan.Zero,
                    Duration = TimeSpan.Zero
                });

                _library.SetProperty(context, "bluray-device", device);
                _library.Command(context, "loadfile", $"bd://{resolution.Selector}", "replace");
                _paused = false;
            }, cancellationToken).ConfigureAwait(false);

            await logger.InfoAsync($"Submitted Blu-ray title {titleIndex + 1} request for {device}.", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _discDiscoveryGate.Release();
        }
    }

    /// <summary>
    /// Returns the libbluray edition list for the disc, probing it headlessly on first
    /// use. The probe is the only trustworthy source of libbluray title indices; without
    /// it, indexed playback cannot be verified and the caller must refuse.
    /// </summary>
    private async Task<IReadOnlyList<BluRayEditionInfo>> EnsureBluRayEditionMapAsync(
        DiscSource disc,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        if (_bluRayEditionCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var device = Path.GetPathRoot(disc.DriveRoot) ?? disc.DriveRoot;
        var editions = await Task.Run(
                () => ProbeBluRayEditions(device, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);

        if (editions.Count == 0)
        {
            throw new OpticalTitleUnavailableException(
                "Parallax could not enumerate the disc's libbluray editions, so no Blu-ray title can be " +
                "verified against the disc. The title was not started rather than risking playback of the wrong program.");
        }

        _bluRayEditionCache[cacheKey] = editions;
        return editions;
    }

    /// <summary>
    /// Headless equivalent of the DVD runtime probe: opens the disc on a scratch libmpv
    /// context with no audio/video output and reads <c>edition-list</c> once the default
    /// title has loaded. Runs on a thread-pool thread; returns an empty list on any
    /// failure so the caller refuses instead of guessing.
    /// </summary>
    private IReadOnlyList<BluRayEditionInfo> ProbeBluRayEditions(string device, CancellationToken cancellationToken)
    {
        nint probe = nint.Zero;
        try
        {
            ConfigureDiscDecryptionProvider();
            if (!_library.TryLoad(out var loadError))
            {
                throw new InvalidOperationException(loadError ?? "libmpv could not be loaded.");
            }

            probe = _library.Create();
            _library.SetOption(probe, "terminal", "no");
            _library.SetOption(probe, "osc", "no");
            _library.SetOption(probe, "idle", "yes");
            _library.SetOption(probe, "keep-open", "no");
            _library.SetOption(probe, "force-window", "no");
            _library.SetOption(probe, "vo", "null");
            _library.SetOption(probe, "ao", "null");
            _library.SetOption(probe, "pause", "yes");
            _library.Initialize(probe);
            _library.SetProperty(probe, "bluray-device", device);
            _library.Command(probe, "loadfile", "bd://", "replace");

            var loaded = false;
            string? failure = null;
            var timeout = Stopwatch.StartNew();
            while (!loaded && failure is null && timeout.Elapsed < TimeSpan.FromSeconds(90))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var item in _library.DrainEvents(probe))
                {
                    if (item.Kind == "file-loaded")
                        loaded = true;
                    else if (item.Kind == "end-file" && item.Level == "error")
                        failure = item.Message;
                }

                if (!loaded && failure is null)
                    Thread.Sleep(100);
            }

            if (!loaded)
            {
                logger.WarningAsync(
                        $"Blu-ray edition probe for {device} did not load the disc " +
                        (failure is not null ? $"({failure})" : $"(timeout after {timeout.Elapsed.TotalSeconds:0} s)") +
                        "; indexed titles will be refused.",
                        CancellationToken.None)
                    .GetAwaiter().GetResult();
                return [];
            }

            var count = ParseInt(_library.GetProperty(probe, "edition-list/count"));
            var editions = new List<BluRayEditionInfo>(count);
            for (var index = 0; index < count; index++)
            {
                var prefix = $"edition-list/{index}/";
                var selector = ParseInt(_library.GetProperty(probe, prefix + "id"), index);
                var title = _library.GetProperty(probe, prefix + "title") ?? string.Empty;
                var (duration, playlist) = BluRayEditionResolver.ParseEditionTitle(title);
                editions.Add(new BluRayEditionInfo(selector, duration, playlist, title));
            }

            var withPlaylist = editions.Count(edition => !string.IsNullOrWhiteSpace(edition.Playlist));
            logger.InfoAsync(
                    $"Blu-ray edition map for {device}: {editions.Count} libbluray editions " +
                    $"({withPlaylist} with playlist identity).",
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            return editions;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.WarningAsync(
                    $"Blu-ray edition probe for {device} failed: {ex.Message}. Indexed titles will be refused.",
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            return [];
        }
        finally
        {
            if (probe != nint.Zero)
            {
                try { _library.TerminateDestroy(probe); }
                catch { }
            }
        }
    }

    /// <summary>
    /// Post-load refusal-over-guess check for indexed Blu-ray playback: the content that
    /// actually loaded must agree with the requested MakeMKV title on runtime (within
    /// tolerance) and chapter count (exact, when known). A contradiction stops playback
    /// rather than letting an unrelated title play silently.
    /// </summary>
    private async Task VerifyLoadedBluRayTitleAsync(nint context, CancellationToken cancellationToken)
    {
        var requested = _requestedBluRayTitle;
        if (requested is null)
            return;

        var selector = _activeBluRayEditionSelector;
        var actualDuration = ParseSeconds(_library.GetProperty(context, "duration"));
        var actualChapterCount = ParseInt(_library.GetProperty(context, "chapter-list/count"), -1);

        var durationOk = requested.Duration <= TimeSpan.Zero || actualDuration <= TimeSpan.Zero ||
            (actualDuration - requested.Duration).Duration() <= BluRayEditionResolver.DurationTolerance;
        var chaptersOk = requested.ChapterCount <= 0 || actualChapterCount < 0 ||
            actualChapterCount == requested.ChapterCount;

        if (durationOk && chaptersOk)
        {
            await logger.InfoAsync(
                $"Blu-ray title reconciled: requested MakeMKV title {requested.DisplayNumber} ({DescribeBluRayTitle(requested)}), " +
                $"libbluray edition {selector} (title {selector + 1}), actual runtime={FormatDuration(actualDuration)}, " +
                $"chapters={actualChapterCount} — verified.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var detail =
            $"requested MakeMKV title {requested.DisplayNumber} ({DescribeBluRayTitle(requested)}), but libbluray edition " +
            $"{selector} (title {selector + 1}) loaded runtime={FormatDuration(actualDuration)}, chapters={actualChapterCount}";
        LastPlaybackError = "The loaded Blu-ray content does not match the selected title. Playback was stopped.";
        await logger.ErrorAsync(
            $"Blu-ray title verification failed: {detail}. Playback was stopped rather than risk playing the wrong title.",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        try { _library.Command(context, "stop"); }
        catch (Exception ex)
        {
            await logger.WarningAsync($"Could not stop the mismatched Blu-ray title: {ex.Message}", cancellationToken).ConfigureAwait(false);
        }

        SetSnapshot(Snapshot with
        {
            State = PlaybackState.Stopped,
            Position = TimeSpan.Zero,
            Duration = TimeSpan.Zero
        });
    }

    private static string DescribeBluRayTitle(BluRayTitle title)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(title.Playlist)) parts.Add(title.Playlist!);
        if (title.Duration > TimeSpan.Zero) parts.Add(FormatDuration(title.Duration));
        if (title.ChapterCount > 0) parts.Add($"{title.ChapterCount} chapters");
        return parts.Count > 0 ? string.Join(", ", parts) : "no identifying metadata";
    }

    // A discovered title may only be reported as actively playing while playback is
    // genuinely live. StopAsync keeps the previous Source on the stopped snapshot, so
    // the state check is what prevents a stale PLAYING badge after stop or fault.
    internal static bool HasLiveOpticalPlayback(PlaybackSnapshot snapshot, string sourcePrefix) =>
        snapshot.State is PlaybackState.Loading or PlaybackState.Playing or PlaybackState.Paused &&
        snapshot.Source?.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase) == true;

    public async Task<IReadOnlyList<BluRayTitle>> GetBluRayTitlesAsync(DiscSource disc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disc);
        if (disc.Kind != DiscKind.BluRay || !disc.IsReady)
            return [];

        var device = Path.GetPathRoot(disc.DriveRoot) ?? disc.DriveRoot;
        var cacheKey = EstablishDiscIdentity(disc);
        var wasCurrentDisc = string.Equals(_loadedBluRayCacheKey, cacheKey, StringComparison.OrdinalIgnoreCase);
        _activeBluRayDevice = device;

        var hasLiveDiscPlayback = wasCurrentDisc && HasLiveOpticalPlayback(Snapshot, "Blu-ray disc");
        var active = hasLiveDiscPlayback ? _activeBluRayTitleIndex : -1;
        var count = 0;

        IReadOnlyList<BluRayTitle> discovered;
        await _discDiscoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_bluRayTitleCache.TryGetValue(cacheKey, out var cached))
            {
                discovered = cached;
                await logger.InfoAsync($"Using cached Blu-ray title metadata for {disc.DisplayName} ({cached.Count} titles).", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                discovered = await DiscoverTitlesWithMakeMkvAsync(disc, cancellationToken).ConfigureAwait(false);
                if (discovered.Count > 0)
                    _bluRayTitleCache[cacheKey] = discovered;
            }
        }
        finally
        {
            _discDiscoveryGate.Release();
        }

        if (discovered.Count == 0)
        {
            return Enumerable.Range(0, count)
                .Select(index => new BluRayTitle(index, index == active, TimeSpan.Zero))
                .ToArray();
        }

        if (active < 0 && hasLiveDiscPlayback)
        {
            active = discovered.OrderByDescending(title => title.Duration).First().Index;
            _activeBluRayTitleIndex = active;
        }

        var longest = discovered.Max(title => title.Duration);
        var durationGroups = discovered
            .Where(title => title.Duration > TimeSpan.Zero)
            .GroupBy(title => Math.Round(title.Duration.TotalSeconds / 2d))
            .ToDictionary(group => group.Key, group => group.Count());

        return discovered
            .Select(title => title with
            {
                IsActive = title.Index == active,
                IsLikelyMainFeature = longest > TimeSpan.Zero && title.Duration >= longest - TimeSpan.FromSeconds(2),
                IsLikelyDuplicate = title.Duration > TimeSpan.Zero &&
                    durationGroups.TryGetValue(Math.Round(title.Duration.TotalSeconds / 2d), out var matches) && matches > 1
            })
            .OrderByDescending(title => title.IsLikelyMainFeature)
            .ThenByDescending(title => title.Duration)
            .ThenBy(title => title.Index)
            .ToArray();
    }

    public async Task OpenDvdAsync(DiscSource disc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disc);
        if (disc.Kind != DiscKind.Dvd)
            throw new InvalidOperationException("The selected optical source is not a DVD.");
        if (!disc.IsReady)
            throw new InvalidOperationException("The DVD drive is not ready.");

        await _discDiscoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        long generation = -1;
        try
        {
            var device = Path.GetPathRoot(disc.DriveRoot) ?? disc.DriveRoot;
            _activeDvdDevice = device;
            _activeDvdDisplayName = CleanDiscTitle(disc.DisplayName, device, "DVD Disc");
            _loadedDvdCacheKey = EstablishDiscIdentity(disc);
            _activeDvdTitleIndex = _dvdTitleCache.TryGetValue(_loadedDvdCacheKey, out var cachedDvdTitles) && cachedDvdTitles.Count > 0
                ? cachedDvdTitles.OrderByDescending(title => title.Duration).First().Index
                : -1;
            _requestedDvdMetadataIndex = _activeDvdTitleIndex;
            _activeDvdPlaybackTitleNumber = -1;
            _dvdHostReconciliationPending = true;
            LastPlaybackError = null;

            await StopInternalPlaybackAsync(cancellationToken).ConfigureAwait(false);
            generation = BeginDvdHostGeneration();
            SetSnapshot(Snapshot with
            {
                State = PlaybackState.Loading,
                Source = $"DVD disc • {device}",
                Title = CleanDiscTitle(disc.DisplayName, device, "DVD Disc"),
                Position = TimeSpan.Zero,
                Duration = TimeSpan.Zero
            });

            _dvdHost ??= _dvdHostFactory(logger);
            await _dvdHost.StartAsync(
                _videoWindowHandle,
                disc,
                titleNumber: null,
                Snapshot.Volume,
                Snapshot.IsMuted,
                cancellationToken).ConfigureAwait(false);
            await logger.InfoAsync($"Started isolated DVD playback host for the longest title on {device}.", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await AbandonDvdHostGenerationAsync(generation, ex).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _discDiscoveryGate.Release();
        }
    }

    public async Task OpenDvdTitleAsync(DiscSource disc, int titleIndex, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disc);
        if (disc.Kind != DiscKind.Dvd)
            throw new InvalidOperationException("The selected optical source is not a DVD.");
        if (!disc.IsReady)
            throw new InvalidOperationException("The DVD drive is not ready.");
        if (titleIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(titleIndex));

        await _discDiscoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        long generation = -1;
        try
        {
            var device = Path.GetPathRoot(disc.DriveRoot) ?? disc.DriveRoot;
            _activeDvdDevice = device;
            _activeDvdDisplayName = CleanDiscTitle(disc.DisplayName, device, "DVD Disc");
            _loadedDvdCacheKey = EstablishDiscIdentity(disc);

            if (_dvdTitleCache.TryGetValue(_loadedDvdCacheKey, out var cachedTitles))
            {
                var selected = cachedTitles.FirstOrDefault(item => item.Index == titleIndex)
                    ?? throw new InvalidOperationException($"DVD title {titleIndex + 1} is not present in the current disc metadata.");
                if (selected.Duration > TimeSpan.FromHours(12))
                    throw new InvalidOperationException("This DVD entry appears to be an aggregate navigation title and is not safe for direct playback.");

                if (!HasDvdPlaybackMapping(_loadedDvdCacheKey, titleIndex))
                    await EnsureDvdFullTitleMapAsync(disc, cachedTitles, cancellationToken).ConfigureAwait(false);

                if (!HasDvdPlaybackMapping(_loadedDvdCacheKey, titleIndex))
                {
                    throw new OpticalTitleUnavailableException(
                        $"Parallax could not confidently match DVD metadata title {titleIndex + 1} to a playable DVD title" +
                        $"{DescribeDvdMapOutcome(_loadedDvdCacheKey, titleIndex)}. " +
                        "The title was not started rather than risking playback of the wrong program.");
                }
            }

            var dvdTitleNumber = ResolveDvdPlaybackTitleNumber(_loadedDvdCacheKey, titleIndex);
            _requestedDvdMetadataIndex = titleIndex;
            _activeDvdPlaybackTitleNumber = dvdTitleNumber;
            _dvdHostReconciliationPending = true;
            _activeDvdTitleIndex = titleIndex;
            LastPlaybackError = null;

            await StopInternalPlaybackAsync(cancellationToken).ConfigureAwait(false);
            generation = BeginDvdHostGeneration();
            SetSnapshot(Snapshot with
            {
                State = PlaybackState.Loading,
                Source = $"DVD disc • {device} • title {dvdTitleNumber + 1}",
                Title = $"{CleanDiscTitle(disc.DisplayName, device, "DVD Disc")} — Title {dvdTitleNumber + 1}",
                Position = TimeSpan.Zero,
                Duration = TimeSpan.Zero
            });

            _dvdHost ??= _dvdHostFactory(logger);
            await _dvdHost.StartAsync(
                _videoWindowHandle,
                disc,
                dvdTitleNumber,
                Snapshot.Volume,
                Snapshot.IsMuted,
                cancellationToken).ConfigureAwait(false);
            var mappingKnown = HasDvdPlaybackMapping(_loadedDvdCacheKey, titleIndex);
            await logger.InfoAsync(
                $"Started isolated DVD playback host for DVD selector {dvdTitleNumber} (title {dvdTitleNumber + 1}) on {device} for metadata title {titleIndex + 1}{(mappingKnown ? " using the verified full-disc map" : " using a provisional fallback")}.",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await AbandonDvdHostGenerationAsync(generation, ex).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _discDiscoveryGate.Release();
        }
    }

    public async Task<IReadOnlyList<BluRayTitle>> GetDvdTitlesAsync(DiscSource disc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disc);
        if (disc.Kind != DiscKind.Dvd || !disc.IsReady)
            return [];

        var device = Path.GetPathRoot(disc.DriveRoot) ?? disc.DriveRoot;
        var cacheKey = EstablishDiscIdentity(disc);
        var wasCurrentDisc = string.Equals(_loadedDvdCacheKey, cacheKey, StringComparison.OrdinalIgnoreCase);
        _activeDvdDevice = device;

        var hasLiveDiscPlayback = wasCurrentDisc && HasLiveOpticalPlayback(Snapshot, "DVD disc");
        var active = hasLiveDiscPlayback ? _activeDvdTitleIndex : -1;
        var count = 0;

        IReadOnlyList<BluRayTitle> discovered;
        await _discDiscoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_dvdTitleCache.TryGetValue(cacheKey, out var cached))
            {
                discovered = cached;
                await logger.InfoAsync($"Using cached DVD title metadata for {disc.DisplayName} ({cached.Count} titles).", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                discovered = await DiscoverDvdTitlesWithMakeMkvAsync(disc, cancellationToken).ConfigureAwait(false);
                if (discovered.Count > 0)
                    _dvdTitleCache[cacheKey] = discovered;
            }
        }
        finally
        {
            _discDiscoveryGate.Release();
        }

        var aggregateCount = discovered.Count(title => title.Duration > TimeSpan.FromHours(12));
        if (aggregateCount > 0)
        {
            discovered = discovered
                .Where(title => title.Duration <= TimeSpan.FromHours(12))
                .ToArray();
            await logger.InfoAsync($"Hidden {aggregateCount} aggregate DVD navigation title{(aggregateCount == 1 ? string.Empty : "s")} longer than 12 hours.", cancellationToken)
                .ConfigureAwait(false);
        }

        if (discovered.Count == 0)
        {
            return Enumerable.Range(0, count)
                .Select(index => new BluRayTitle(index, index == active, TimeSpan.Zero, SourceKind: DiscKind.Dvd))
                .ToArray();
        }

        if (active < 0 && hasLiveDiscPlayback)
        {
            active = discovered.OrderByDescending(title => title.Duration).First().Index;
            _activeDvdTitleIndex = active;
        }

        var episodeRange = discovered
            .Where(title => title.Duration >= TimeSpan.FromMinutes(18) && title.Duration <= TimeSpan.FromMinutes(75))
            .ToArray();
        var hasEpisodeSet = episodeRange.Length >= 2;
        var longest = discovered.Max(title => title.Duration);
        var durationGroups = discovered
            .Where(title => title.Duration > TimeSpan.Zero)
            .GroupBy(title => Math.Round(title.Duration.TotalSeconds / 2d))
            .ToDictionary(group => group.Key, group => group.Count());
        Dictionary<int, int>? verifiedMappings = null;
        lock (_dvdMappingSync)
        {
            if (_dvdPlaybackTitleByMetadataIndex.TryGetValue(cacheKey, out var currentMappings))
                verifiedMappings = new Dictionary<int, int>(currentMappings);
        }

        return discovered
            .Select(title =>
            {
                var playbackTitleNumber = -1;
                var hasVerifiedMapping = verifiedMappings is not null &&
                    verifiedMappings.TryGetValue(title.Index, out playbackTitleNumber);
                return title with
                {
                    IsActive = title.Index == active,
                    IsLikelyEpisode = hasEpisodeSet && title.Duration >= TimeSpan.FromMinutes(18) && title.Duration <= TimeSpan.FromMinutes(75),
                    IsLikelyMainFeature = !hasEpisodeSet && longest > TimeSpan.Zero && title.Duration >= longest - TimeSpan.FromSeconds(2),
                    IsLikelyDuplicate = title.Duration > TimeSpan.Zero &&
                        durationGroups.TryGetValue(Math.Round(title.Duration.TotalSeconds / 2d), out var matches) && matches > 1,
                    SourceKind = DiscKind.Dvd,
                    DvdPlaybackTitleNumber = hasVerifiedMapping ? playbackTitleNumber : (int?)null,
                    IsDvdMappingVerified = hasVerifiedMapping
                };
            })
            .OrderByDescending(title => title.IsActive)
            .ThenByDescending(title => title.IsLikelyEpisode)
            .ThenByDescending(title => title.IsLikelyMainFeature)
            .ThenByDescending(title => title.Duration)
            .ThenBy(title => title.Index)
            .ToArray();
    }

    private async Task<IReadOnlyList<BluRayTitle>> DiscoverTitlesWithMakeMkvAsync(DiscSource disc, CancellationToken cancellationToken)
    {
        var executable = new[]
        {
            @"C:\Program Files (x86)\MakeMKV\makemkvcon64.exe",
            @"C:\Program Files\MakeMKV\makemkvcon64.exe",
            @"C:\Program Files (x86)\MakeMKV\makemkvcon.exe",
            @"C:\Program Files\MakeMKV\makemkvcon.exe"
        }.FirstOrDefault(File.Exists);
        if (executable is null) return [];

        var device = Path.GetPathRoot(disc.DriveRoot) ?? disc.DriveRoot;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = $"-r --minlength=0 info dev:{QuoteArgument(device)}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(startInfo);
            if (process is null) return [];
            using var cancellationRegistration = cancellationToken.Register(() => TryKillProcess(process));
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                await logger.WarningAsync($"MakeMKV title discovery exited with code {process.ExitCode}: {error}", cancellationToken).ConfigureAwait(false);
                return [];
            }

            var builders = new Dictionary<int, TitleInfoBuilder>();
            foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var match = Regex.Match(rawLine, "^TINFO:(\\d+),(\\d+),\\d+,\"(.*)\"$");
                if (!match.Success) continue;
                var index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var code = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                var value = match.Groups[3].Value.Replace("\"\"", "\"");
                if (!builders.TryGetValue(index, out var builder)) builders[index] = builder = new TitleInfoBuilder(index);
                switch (code)
                {
                    case 2: builder.Name = value; break;
                    case 8: builder.ChapterCount = ParseInt(value); break;
                    case 9: if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var duration)) builder.Duration = duration; break;
                    case 16: builder.Playlist = value; break;
                    case 30: if (string.IsNullOrWhiteSpace(builder.Name)) builder.Name = value; break;
                    case 49: if (string.IsNullOrWhiteSpace(builder.Name)) builder.Name = value; break;
                }
            }

            var result = builders.Values
                .Where(builder => builder.Duration > TimeSpan.Zero || builder.ChapterCount > 0 || !string.IsNullOrWhiteSpace(builder.Playlist))
                .Select(builder => new BluRayTitle(builder.Index, false, builder.Duration, builder.ChapterCount, builder.Playlist, builder.Name))
                .ToArray();
            await logger.InfoAsync($"MakeMKV reported {result.Length} Blu-ray titles for {device}.", cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await logger.InfoAsync($"MakeMKV title discovery cancelled for {disc.DisplayName}.", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await logger.WarningAsync($"MakeMKV title discovery failed: {ex.Message}", cancellationToken).ConfigureAwait(false);
            return [];
        }
    }

    private async Task<IReadOnlyList<BluRayTitle>> DiscoverDvdTitlesWithMakeMkvAsync(DiscSource disc, CancellationToken cancellationToken)
    {
        var executable = new[]
        {
            @"C:\Program Files (x86)\MakeMKV\makemkvcon64.exe",
            @"C:\Program Files\MakeMKV\makemkvcon64.exe",
            @"C:\Program Files (x86)\MakeMKV\makemkvcon.exe",
            @"C:\Program Files\MakeMKV\makemkvcon.exe"
        }.FirstOrDefault(File.Exists);
        if (executable is null) return [];

        var device = Path.GetPathRoot(disc.DriveRoot) ?? disc.DriveRoot;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = $"-r --minlength=0 info dev:{QuoteArgument(device)}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(startInfo);
            if (process is null) return [];
            using var cancellationRegistration = cancellationToken.Register(() => TryKillProcess(process));
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                await logger.WarningAsync($"MakeMKV DVD title discovery exited with code {process.ExitCode}: {error}", cancellationToken).ConfigureAwait(false);
                return [];
            }

            var builders = new Dictionary<int, TitleInfoBuilder>();
            foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var match = Regex.Match(rawLine, "^TINFO:(\\d+),(\\d+),\\d+,\"(.*)\"$");
                if (!match.Success) continue;
                var index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var code = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                var value = match.Groups[3].Value.Replace("\"\"", "\"");
                if (!builders.TryGetValue(index, out var builder)) builders[index] = builder = new TitleInfoBuilder(index);
                switch (code)
                {
                    case 2: builder.Name = value; break;
                    case 8: builder.ChapterCount = ParseInt(value); break;
                    case 9: if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var duration)) builder.Duration = duration; break;
                    case 16: builder.Playlist = value; break;
                    case 30: if (string.IsNullOrWhiteSpace(builder.Name)) builder.Name = value; break;
                    case 49: if (string.IsNullOrWhiteSpace(builder.Name)) builder.Name = value; break;
                }
            }

            var result = builders.Values
                .Where(builder => builder.Duration > TimeSpan.Zero || builder.ChapterCount > 0 || !string.IsNullOrWhiteSpace(builder.Name))
                .Select(builder => new BluRayTitle(
                    builder.Index,
                    false,
                    builder.Duration,
                    builder.ChapterCount,
                    builder.Playlist,
                    builder.Name,
                    SourceKind: DiscKind.Dvd))
                .ToArray();
            await logger.InfoAsync($"MakeMKV reported {result.Length} DVD titles for {device}.", cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await logger.InfoAsync($"MakeMKV DVD title discovery cancelled for {disc.DisplayName}.", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await logger.WarningAsync($"MakeMKV DVD title discovery failed: {ex.Message}", cancellationToken).ConfigureAwait(false);
            return [];
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort only. Cancellation still unwinds the discovery task.
        }
    }

    private bool HasDvdPlaybackMapping(string cacheKey, int metadataIndex)
    {
        lock (_dvdMappingSync)
        {
            return _dvdPlaybackTitleByMetadataIndex.TryGetValue(cacheKey, out var mapping) &&
                mapping.TryGetValue(metadataIndex, out var selector) &&
                selector >= 0;
        }
    }

    private async Task EnsureDvdFullTitleMapAsync(
        DiscSource disc,
        IReadOnlyList<BluRayTitle> metadataTitles,
        CancellationToken cancellationToken)
    {
        // Called only for the currently loaded DVD: OpenDvdTitleAsync establishes the
        // identity first, and reusing that key keeps degraded/insufficient identities
        // consistent instead of re-establishing them per call.
        var cacheKey = _loadedDvdCacheKey ?? EstablishDiscIdentity(disc);
        lock (_dvdMappingSync)
        {
            if (_dvdFullMapCache.Contains(cacheKey))
                return;
        }

        var probePath = Path.Combine(AppContext.BaseDirectory, "dvd-probe", "ParallaxStudio.DvdProbe.exe");
        if (!File.Exists(probePath))
            throw new InvalidOperationException($"DVD title-map probe is missing: {probePath}");

        var device = Path.GetPathRoot(disc.DriveRoot) ?? disc.DriveRoot;
        var startInfo = new ProcessStartInfo
        {
            FileName = probePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(device);
        startInfo.ArgumentList.Add("0");

        await logger.InfoAsync($"Building full DVD title map for {device} before playback using the first authored title as the probe anchor.", cancellationToken).ConfigureAwait(false);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException("The isolated DVD title-map probe could not be started.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            var exitTask = process.WaitForExitAsync(cancellationToken);
            var completed = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(60), cancellationToken)).ConfigureAwait(false);
            if (!ReferenceEquals(completed, exitTask))
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException("The isolated DVD title-map probe did not finish within 60 seconds.");
            }
            await exitTask.ConfigureAwait(false);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = (await stderrTask.ConfigureAwait(false)).Trim();
        if (!string.IsNullOrWhiteSpace(stderr))
            await logger.WarningAsync($"DVD title-map probe error output: {stderr}", cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var unsignedExit = unchecked((uint)process.ExitCode);
            throw new InvalidOperationException($"DVD title mapping failed in the isolated probe (exit 0x{unsignedExit:X8}).");
        }

        await ApplyDvdTitleMapAsync(cacheKey, metadataTitles, ParseDvdEditionMap(stdout), cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyDvdTitleMapAsync(
        string cacheKey,
        IReadOnlyList<BluRayTitle> metadataTitles,
        IReadOnlyList<DvdEditionInfo> editions,
        CancellationToken cancellationToken)
    {
        var result = DvdTitleMatcher.BuildFullMap(metadataTitles, editions);

        await logger.InfoAsync(
            $"DVD title-map inputs: metadata [{string.Join(", ", metadataTitles.OrderBy(title => title.Index).Select(title => $"{title.DisplayNumber}={DvdTitleMatcher.Format(title.Duration)}/{title.ChapterCount}ch"))}]; " +
            $"DVD titles [{string.Join(", ", editions.OrderBy(edition => edition.Selector).Select(edition => $"{edition.Selector + 1}={DvdTitleMatcher.Format(edition.Duration)}"))}].",
            cancellationToken).ConfigureAwait(false);

        lock (_dvdMappingSync)
        {
            _dvdTitleMapResults[cacheKey] = result;
            if (result.Mapping.Count > 0)
            {
                _dvdPlaybackTitleByMetadataIndex[cacheKey] = new Dictionary<int, int>(result.Mapping);
                _dvdFullMapCache.Add(cacheKey);
            }
        }

        var mappedText = string.Join(", ", result.Outcomes
            .Where(outcome => outcome.Kind == DvdTitleMatchKind.Unique)
            .OrderBy(outcome => outcome.MetadataIndex)
            .Select(outcome => $"metadata {outcome.MetadataIndex + 1}->DVD title {outcome.Selector + 1}" +
                (outcome.Basis == DvdTitleMatchBasis.Calibrated ? " (calibrated)" : string.Empty)));
        if (result.Calibration is not null)
            await logger.InfoAsync($"DVD title-map calibration: {result.Calibration}.", cancellationToken).ConfigureAwait(false);
        else if (!string.IsNullOrWhiteSpace(result.CalibrationNote))
            await logger.InfoAsync($"DVD title-map calibration: {result.CalibrationNote}.", cancellationToken).ConfigureAwait(false);
        await logger.InfoAsync($"Full DVD title map built with {result.Mapping.Count} confident matches: {mappedText}.", cancellationToken).ConfigureAwait(false);

        foreach (var outcome in result.Outcomes.Where(outcome => outcome.Kind != DvdTitleMatchKind.Unique))
        {
            await logger.WarningAsync(
                $"DVD metadata title {outcome.MetadataIndex + 1} not mapped ({outcome.Kind.ToString().ToLowerInvariant()}): {outcome.Reason}.",
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Test seam: installs discovered metadata for <paramref name="disc"/> and applies the
    /// production title-map path to probe-style <paramref name="editions"/>, exactly as
    /// EnsureDvdFullTitleMapAsync does after the isolated probe returns.
    /// </summary>
    internal async Task SeedDvdTitleMapForTestingAsync(
        DiscSource disc,
        IReadOnlyList<BluRayTitle> metadataTitles,
        IReadOnlyList<DvdEditionInfo> editions)
    {
        var cacheKey = EstablishDiscIdentity(disc);
        await _discDiscoveryGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _dvdTitleCache[cacheKey] = metadataTitles;
        }
        finally
        {
            _discDiscoveryGate.Release();
        }

        await ApplyDvdTitleMapAsync(cacheKey, metadataTitles, editions, CancellationToken.None).ConfigureAwait(false);
        lock (_dvdMappingSync)
        {
            // The probe ran; do not run the real probe executable for this disc.
            _dvdFullMapCache.Add(cacheKey);
        }
    }

    internal IReadOnlyDictionary<int, int> GetDvdTitleMapForTesting(DiscSource disc)
    {
        lock (_dvdMappingSync)
        {
            return _dvdPlaybackTitleByMetadataIndex.TryGetValue(EstablishDiscIdentity(disc), out var mapping)
                ? new Dictionary<int, int>(mapping)
                : new Dictionary<int, int>();
        }
    }

    private string DescribeDvdMapOutcome(string cacheKey, int metadataIndex)
    {
        lock (_dvdMappingSync)
        {
            if (!_dvdTitleMapResults.TryGetValue(cacheKey, out var result) || result.OutcomeFor(metadataIndex) is not { } outcome)
                return string.Empty;
            return outcome.Kind == DvdTitleMatchKind.Unique
                ? " (its mapping was withdrawn after a conflicting playback check; see the log)"
                : $" ({outcome.Kind.ToString().ToLowerInvariant()}: {outcome.Reason})";
        }
    }

    internal static IReadOnlyList<DvdEditionInfo> ParseDvdEditionMap(string output)
    {
        var result = new List<DvdEditionInfo>();
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!rawLine.StartsWith("DVD_EDITION|", StringComparison.Ordinal)) continue;
            var parts = rawLine.Split('|', 5);
            if (parts.Length != 5 ||
                !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var selector) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
                !double.IsFinite(seconds) || seconds <= 0)
            {
                continue;
            }

            string? title = null;
            try { title = Encoding.UTF8.GetString(Convert.FromBase64String(parts[4])); } catch { }
            result.Add(new DvdEditionInfo(
                selector,
                TimeSpan.FromSeconds(seconds),
                title,
                parts[3] == "1"));
        }
        return result;
    }

    private int ResolveDvdPlaybackTitleNumber(string cacheKey, int metadataIndex)
    {
        lock (_dvdMappingSync)
        {
            if (_dvdPlaybackTitleByMetadataIndex.TryGetValue(cacheKey, out var mapping) &&
                mapping.TryGetValue(metadataIndex, out var playbackTitleNumber) &&
                playbackTitleNumber >= 0)
            {
                return playbackTitleNumber;
            }
        }

        return metadataIndex;
    }

    private async Task ReconcileDvdHostTitleAsync(PlaybackSnapshot hostSnapshot, CancellationToken cancellationToken)
    {
        _dvdHostReconciliationPending = false;
        if (_dvdHost is not { IsActive: true } ||
            string.IsNullOrWhiteSpace(_loadedDvdCacheKey) ||
            !_dvdTitleCache.TryGetValue(_loadedDvdCacheKey, out var metadataTitles) ||
            metadataTitles.Count == 0)
        {
            return;
        }

        IReadOnlyList<MediaChapter> chapters;
        try
        {
            chapters = await _dvdHost.GetChaptersAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await logger.WarningAsync($"DVD title reconciliation could not read chapters: {ex.Message}", cancellationToken).ConfigureAwait(false);
            chapters = [];
        }

        var actualDuration = hostSnapshot.Duration;
        var actualChapterCount = chapters.Count;
        var selector = _activeDvdPlaybackTitleNumber;
        var cacheKey = _loadedDvdCacheKey;
        DvdRuntimeCalibration? calibration;
        lock (_dvdMappingSync)
        {
            calibration = _dvdTitleMapResults.TryGetValue(cacheKey, out var mapResult) ? mapResult.Calibration : null;
        }
        var loaded = DvdTitleMatcher.MatchLoadedTitle(metadataTitles, actualDuration, actualChapterCount, calibration);
        if (loaded.Kind != DvdTitleMatchKind.Unique || loaded.Title is not { } match)
        {
            // Not evidence either way: keep any mapping the full title map established and
            // keep presenting the requested title, but say that it could not be verified.
            await logger.WarningAsync(
                $"DVD title reconciliation could not verify DVD selector {selector} (title {selector + 1}) " +
                $"({loaded.Kind.ToString().ToLowerInvariant()}): {loaded.Reason}. " +
                $"runtime={FormatDuration(actualDuration)}, chapters={actualChapterCount}.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var previousMetadataIndex = _requestedDvdMetadataIndex;

        if (selector >= 0)
        {
            var conflicts = new List<string>();
            lock (_dvdMappingSync)
            {
                if (!_dvdPlaybackTitleByMetadataIndex.TryGetValue(cacheKey, out var mapping))
                {
                    mapping = new Dictionary<int, int>();
                    _dvdPlaybackTitleByMetadataIndex[cacheKey] = mapping;
                }

                // An existing entry that disagrees with this observation means one of the two
                // inferences was wrong, and neither can be preferred. Remove both claims rather
                // than letting the later observation silently overwrite the earlier one.
                foreach (var stale in mapping.Where(pair => pair.Value == selector && pair.Key != match.Index).ToArray())
                {
                    mapping.Remove(stale.Key);
                    conflicts.Add($"metadata title {stale.Key + 1} had been mapped to DVD title {selector + 1}");
                }

                if (mapping.TryGetValue(match.Index, out var mappedSelector) && mappedSelector != selector)
                {
                    mapping.Remove(match.Index);
                    conflicts.Add($"metadata title {match.DisplayNumber} had been mapped to DVD title {mappedSelector + 1}");
                }

                if (conflicts.Count == 0)
                    mapping[match.Index] = selector;
            }

            if (conflicts.Count > 0)
            {
                // The requested title stays the presented one, now unverified.
                await logger.WarningAsync(
                    $"DVD title reconciliation conflict: DVD selector {selector} (title {selector + 1}) loaded runtime={FormatDuration(actualDuration)}, " +
                    $"chapters={actualChapterCount}, which only metadata title {match.DisplayNumber} matches, but {string.Join("; ", conflicts)}. " +
                    "The conflicting mappings were withdrawn; those titles are refused for this disc until Parallax Studio is restarted.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        _activeDvdTitleIndex = match.Index;

        var requestedText = previousMetadataIndex >= 0 ? (previousMetadataIndex + 1).ToString(CultureInfo.InvariantCulture) : "longest";
        await logger.InfoAsync(
            $"DVD title reconciled: requested metadata title {requestedText}, DVD selector {_activeDvdPlaybackTitleNumber} (title {_activeDvdPlaybackTitleNumber + 1}), " +
            $"actual runtime={FormatDuration(actualDuration)}, chapters={actualChapterCount}, " +
            $"matched metadata title {match.DisplayNumber} ({match.DurationText}, {match.ChapterCount} chapters).",
            cancellationToken).ConfigureAwait(false);
    }

    private PlaybackSnapshot ApplyDvdHostPresentation(PlaybackSnapshot hostSnapshot)
    {
        if (string.IsNullOrWhiteSpace(_loadedDvdCacheKey) ||
            !_dvdTitleCache.TryGetValue(_loadedDvdCacheKey, out var titles))
        {
            return hostSnapshot;
        }

        var activeTitle = titles.FirstOrDefault(title => title.Index == _activeDvdTitleIndex);
        var programText = _activeDvdPlaybackTitleNumber >= 0
            ? $" • DVD title {_activeDvdPlaybackTitleNumber + 1}"
            : string.Empty;
        var titleText = activeTitle is null
            ? _activeDvdDisplayName
            : $"{_activeDvdDisplayName} — {activeTitle.PrimaryText}";

        return hostSnapshot with
        {
            Source = $"DVD disc • {_activeDvdDevice}{programText}",
            Title = titleText
        };
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");

    // Single source of optical cache identity. Strong identities derive from the disc
    // itself and are drive-letter independent; degraded identities stay drive-scoped;
    // insufficient identities are made unique per establishment so cached optical title
    // and mapping data can never be reused across a media transition that left no
    // distinguishing evidence. Internal for deterministic fallback regression coverage.
    internal string EstablishDiscIdentity(DiscSource disc) =>
        EstablishOpticalIdentity(OpticalDiscIdentity.Build(disc));

    // Test seam: exercises the insufficient-identity non-reuse mechanism with a
    // deterministic identity, independent of the host drive's evidence.
    internal string EstablishDiscIdentityForTesting(OpticalDiscIdentityInfo identity) =>
        EstablishOpticalIdentity(identity);

    private string EstablishOpticalIdentity(OpticalDiscIdentityInfo identity)
    {
        if (_establishedOpticalIdentities.Add(identity.Key))
        {
            _ = logger.InfoAsync(identity.Classification switch
            {
                OpticalIdentityClassification.Strong =>
                    $"Optical identity established: {identity.Summary}.",
                OpticalIdentityClassification.Degraded =>
                    $"Optical identity degraded: {identity.Summary}; session caches remain drive-scoped.",
                _ =>
                    $"Optical identity insufficient: {identity.Summary}; cached optical titles and mappings will not be reused."
            });
        }

        return identity.IsReusable
            ? identity.Key
            : $"{identity.Key}|SEQ{Interlocked.Increment(ref _insufficientIdentitySequence).ToString(CultureInfo.InvariantCulture)}";
    }

    private static string QuoteArgument(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

    private sealed class TitleInfoBuilder(int index)
    {
        public int Index { get; } = index;
        public TimeSpan Duration { get; set; }
        public int ChapterCount { get; set; }
        public string? Playlist { get; set; }
        public string? Name { get; set; }
    }

    public async Task TogglePauseAsync(CancellationToken cancellationToken = default)
    {
        if (_usingDvdHost && _dvdHost is { IsActive: true })
        {
            await _dvdHost.TogglePauseAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // With no actively loading or playing media there is nothing to pause; toggling
        // would only publish a Playing/Paused state the poll loop has to correct.
        if (Snapshot.State is not (PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Loading))
            return;

        await WithInitializedContextAsync(context =>
        {
            _paused = !_paused;
            _library.SetProperty(context, "pause", _paused ? "yes" : "no");
            // During Loading the poll loop owns the published state; only a settled
            // Playing/Paused state is flipped here.
            if (Snapshot.State is PlaybackState.Playing or PlaybackState.Paused)
                SetSnapshot(Snapshot with { State = _paused ? PlaybackState.Paused : PlaybackState.Playing });
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var previous = Snapshot;
        if (_usingDvdHost && _dvdHost is { IsActive: true })
        {
            await _dvdHost.StopAsync(cancellationToken).ConfigureAwait(false);
            await LogPlaybackStoppedAsync(previous, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WithInitializedContextAsync(context =>
        {
            _library.Command(context, "stop");
            _paused = false;
            SetSnapshot(Snapshot with
            {
                State = PlaybackState.Stopped,
                Position = TimeSpan.Zero,
                Duration = TimeSpan.Zero
            });
        }, cancellationToken).ConfigureAwait(false);
        await LogPlaybackStoppedAsync(previous, cancellationToken).ConfigureAwait(false);
    }

    // Stop is a deliberate user (or UI-flow) action, never a poll-loop event, so one
    // INFO line with the previous context is safe and useful for diagnostics.
    private async Task LogPlaybackStoppedAsync(PlaybackSnapshot previous, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(previous.Source) &&
            previous.State is not (PlaybackState.Loading or PlaybackState.Playing or PlaybackState.Paused))
            return;

        await logger.InfoAsync(
            $"Playback stopped: source={previous.Source ?? "none"}, title={previous.Title}, previous state={previous.State}.",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SeekRelativeAsync(double seconds, CancellationToken cancellationToken = default)
    {
        if (_usingDvdHost && _dvdHost is { IsActive: true })
        {
            await _dvdHost.SeekRelativeAsync(seconds, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WithInitializedContextAsync(context =>
            _library.Command(context, "seek", seconds.ToString("0.###", CultureInfo.InvariantCulture), "relative+exact"),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SeekAbsoluteAsync(double seconds, CancellationToken cancellationToken = default)
    {
        if (_usingDvdHost && _dvdHost is { IsActive: true })
        {
            await _dvdHost.SeekAbsoluteAsync(seconds, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WithInitializedContextAsync(context =>
            _library.Command(context, "seek", Math.Max(0, seconds).ToString("0.###", CultureInfo.InvariantCulture), "absolute+exact"),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default)
    {
        if (_usingDvdHost && _dvdHost is { IsActive: true })
        {
            await _dvdHost.SetMutedAsync(muted, cancellationToken).ConfigureAwait(false);
            SetSnapshot(Snapshot with { IsMuted = muted });
            return;
        }

        await WithInitializedContextAsync(context =>
        {
            _library.SetProperty(context, "mute", muted ? "yes" : "no");
            SetSnapshot(Snapshot with { IsMuted = muted });
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default)
    {
        volume = Math.Clamp(volume, 0, 100);
        SetSnapshot(Snapshot with { Volume = volume });
        if (_usingDvdHost && _dvdHost is { IsActive: true })
        {
            await _dvdHost.SetVolumeAsync(volume, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!IsInitialized) return;

        await WithInitializedContextAsync(context =>
            _library.SetProperty(context, "volume", volume.ToString("0.##", CultureInfo.InvariantCulture)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MediaTrack>> GetTracksAsync(CancellationToken cancellationToken = default)
    {
        if (_usingDvdHost && _dvdHost is { IsActive: true })
            return await _dvdHost.GetTracksAsync(cancellationToken).ConfigureAwait(false);

        var result = new List<MediaTrack>();
        await WithInitializedContextAsync(context =>
        {
            var count = ParseInt(_library.GetProperty(context, "track-list/count"));
            var videoWidth = ParseInt(_library.GetProperty(context, "video-params/w"));
            var videoHeight = ParseInt(_library.GetProperty(context, "video-params/h"));
            var videoFps = ParseDouble(FirstNonEmpty(
                _library.GetProperty(context, "container-fps"),
                _library.GetProperty(context, "estimated-vf-fps")));
            var videoGamma = _library.GetProperty(context, "video-params/gamma");
            var videoDetails = BuildVideoDetails(videoWidth, videoHeight, videoFps, videoGamma);
            var activeAudioId = ParseInt(_library.GetProperty(context, "aid"), -1);
            var activeSubtitleId = ParseInt(_library.GetProperty(context, "sid"), -1);
            var activeVideoId = ParseInt(_library.GetProperty(context, "vid"), -1);
            for (var index = 0; index < count; index++)
            {
                var prefix = $"track-list/{index}/";
                var type = _library.GetProperty(context, prefix + "type");
                var kind = type switch
                {
                    "video" => MediaTrackKind.Video,
                    "audio" => MediaTrackKind.Audio,
                    "sub" => MediaTrackKind.Subtitle,
                    _ => (MediaTrackKind?)null
                };
                if (kind is null) continue;

                var id = ParseInt(_library.GetProperty(context, prefix + "id"));
                var title = _library.GetProperty(context, prefix + "title");
                var language = _library.GetProperty(context, prefix + "lang");
                var codec = _library.GetProperty(context, prefix + "codec");
                var channels = _library.GetProperty(context, prefix + "demux-channel-count");
                var channelLayout = _library.GetProperty(context, prefix + "demux-channels");
                var sampleRate = ParseInt(_library.GetProperty(context, prefix + "demux-samplerate"));
                var audioLayout = FirstNonEmpty(channelLayout, !string.IsNullOrWhiteSpace(channels) ? $"{channels} ch" : null);
                var audioDetails = string.Join(" • ", new[]
                {
                    audioLayout,
                    sampleRate > 0 ? $"{sampleRate / 1000d:0.#} kHz" : null
                }.Where(value => !string.IsNullOrWhiteSpace(value)));
                var details = kind switch
                {
                    MediaTrackKind.Audio => string.IsNullOrWhiteSpace(audioDetails) ? null : audioDetails,
                    MediaTrackKind.Video => videoDetails,
                    _ => null
                };
                var label = string.IsNullOrWhiteSpace(title)
                    ? $"{kind} {id}"
                    : title;

                result.Add(new MediaTrack(
                    id, kind.Value, label, language, codec, details,
                    IsYes(_library.GetProperty(context, prefix + "default")),
                    IsYes(_library.GetProperty(context, prefix + "forced")),
                    kind.Value switch
                    {
                        MediaTrackKind.Audio => id == activeAudioId,
                        MediaTrackKind.Subtitle => id == activeSubtitleId,
                        MediaTrackKind.Video => id == activeVideoId,
                        _ => IsYes(_library.GetProperty(context, prefix + "selected"))
                    }));
            }
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<MediaChapter>> GetChaptersAsync(CancellationToken cancellationToken = default)
    {
        if (_usingDvdHost && _dvdHost is { IsActive: true })
            return await _dvdHost.GetChaptersAsync(cancellationToken).ConfigureAwait(false);

        var result = new List<MediaChapter>();
        await WithInitializedContextAsync(context =>
        {
            var count = ParseInt(_library.GetProperty(context, "chapter-list/count"));
            var current = ParseInt(_library.GetProperty(context, "chapter"), -1);
            for (var index = 0; index < count; index++)
            {
                var prefix = $"chapter-list/{index}/";
                var title = _library.GetProperty(context, prefix + "title");
                var start = ParseSeconds(_library.GetProperty(context, prefix + "time"));
                result.Add(new MediaChapter(index, string.IsNullOrWhiteSpace(title) ? $"Chapter {index + 1}" : title, start, index == current));
            }
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task SelectTrackAsync(MediaTrack track, CancellationToken cancellationToken = default)
    {
        if (_usingDvdHost && _dvdHost is { IsActive: true })
        {
            await _dvdHost.SelectTrackAsync(track, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WithInitializedContextAsync(context =>
        {
            var property = track.Kind switch
            {
                MediaTrackKind.Audio => "aid",
                MediaTrackKind.Subtitle => "sid",
                MediaTrackKind.Video => "vid",
                _ => throw new ArgumentOutOfRangeException(nameof(track))
            };
            _library.SetProperty(context, property, track.Id <= 0 ? "no" : track.Id.ToString(CultureInfo.InvariantCulture));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task SelectChapterAsync(int chapterIndex, CancellationToken cancellationToken = default)
    {
        if (_usingDvdHost && _dvdHost is { IsActive: true })
        {
            await _dvdHost.SelectChapterAsync(chapterIndex, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WithInitializedContextAsync(context =>
            _library.SetProperty(context, "chapter", Math.Max(0, chapterIndex).ToString(CultureInfo.InvariantCulture)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task LoadExternalSubtitleAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A subtitle path is required.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Subtitle file not found.", fullPath);

        if (_usingDvdHost && _dvdHost is { IsActive: true })
        {
            await _dvdHost.LoadExternalSubtitleAsync(fullPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WithInitializedContextAsync(context => _library.Command(context, "sub-add", fullPath, "select"), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SetSubtitleDelayAsync(double seconds, CancellationToken cancellationToken = default)
    {
        if (_usingDvdHost && _dvdHost is { IsActive: true })
        {
            await _dvdHost.SetSubtitleDelayAsync(seconds, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WithInitializedContextAsync(context =>
            _library.SetProperty(context, "sub-delay", seconds.ToString("0.###", CultureInfo.InvariantCulture)),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task PollPlaybackAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_usingDvdHost)
                {
                    await PollDvdHostAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                List<MpvDiagnosticEvent> diagnosticEvents = [];
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (!IsInitialized) continue;

                    diagnosticEvents.AddRange(_library.DrainEvents(_context));

                    var fileLoaded = diagnosticEvents.Any(item => item.Kind == "file-loaded");
                    var failed = diagnosticEvents.LastOrDefault(item => item.Kind == "end-file" && item.Level == "error");

                    var position = ParseSeconds(_library.GetProperty(_context, "time-pos"));
                    var duration = ParseSeconds(_library.GetProperty(_context, "duration"));
                    var pause = _library.GetProperty(_context, "pause");
                    var idle = _library.GetProperty(_context, "idle-active");
                    var mute = _library.GetProperty(_context, "mute");

                    var playbackTitle = Snapshot.Title;
                    if (Snapshot.Source?.StartsWith(
                            "Blu-ray disc",
                            StringComparison.OrdinalIgnoreCase) == true)
                    {
                        var metadataTitle = _library.GetProperty(
                            _context,
                            "metadata/by-key/Title");

                        if (!string.IsNullOrWhiteSpace(metadataTitle))
                            playbackTitle = metadataTitle.Trim();
                    }

                    _paused = string.Equals(pause, "yes", StringComparison.OrdinalIgnoreCase);
                    var state = Snapshot.State;
                    if (!string.IsNullOrWhiteSpace(Snapshot.Source))
                    {
                        if (failed is not null)
                        {
                            state = PlaybackState.Faulted;
                        }
                        else if (Snapshot.State == PlaybackState.Loading && !fileLoaded)
                        {
                            // Do not report Playing until mpv has completed the final file/title load.
                            state = PlaybackState.Loading;
                        }
                        else
                        {
                            state = string.Equals(idle, "yes", StringComparison.OrdinalIgnoreCase)
                                ? PlaybackState.Stopped
                                : _paused ? PlaybackState.Paused : PlaybackState.Playing;
                        }
                    }

                    var next = Snapshot with
                    {
                        State = state,
                        Title = playbackTitle,
                        Position = position,
                        Duration = duration,
                        IsMuted = string.Equals(mute, "yes", StringComparison.OrdinalIgnoreCase)
                    };

                    if (next != Snapshot) SetSnapshot(next);

                    if (fileLoaded && _bluRayVerificationPending)
                    {
                        // Consume the pending verification only for the load it belongs to;
                        // a superseding load (different source) discards it silently.
                        var verify = string.Equals(Snapshot.Source, _bluRayVerificationSource, StringComparison.Ordinal);
                        _bluRayVerificationPending = false;
                        _bluRayVerificationSource = null;
                        if (verify)
                            await VerifyLoadedBluRayTitleAsync(_context, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    await logger.WarningAsync($"Playback polling failed: {ex.Message}", cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }

                foreach (var item in diagnosticEvents)
                {
                    var message = $"mpv[{item.Prefix}/{item.Level}] {item.Message}";
                    if (item.Level is "error" or "fatal")
                    {
                        LastPlaybackError = item.Message;
                        await logger.ErrorAsync(message, cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    else if (item.Level is "warn" or "warning")
                        await logger.WarningAsync(message, cancellationToken).ConfigureAwait(false);
                    else
                        await logger.InfoAsync(message, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }


    private void ConfigureDiscDecryptionProvider()
    {
        if (!string.IsNullOrWhiteSpace(_discDecryptionProvider))
            return;

        var candidates = new List<string>();
        var configured = Environment.GetEnvironmentVariable("PARALLAX_STUDIO_LIBMMBD");
        if (!string.IsNullOrWhiteSpace(configured))
            candidates.Add(configured);

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
            candidates.Add(Path.Combine(programFilesX86, "MakeMKV", "libmmbd64.dll"));

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
            candidates.Add(Path.Combine(programFiles, "MakeMKV", "libmmbd64.dll"));

        var libMmbd = candidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(File.Exists);

        if (libMmbd is null)
        {
            _discDecryptionProvider = "plain libaacs/libbdplus fallback";
            return;
        }

        Environment.SetEnvironmentVariable("LIBAACS_PATH", libMmbd, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("LIBBDPLUS_PATH", libMmbd, EnvironmentVariableTarget.Process);

        var runtimeDirectory = AppContext.BaseDirectory;
        var runtimeAacs = Path.Combine(runtimeDirectory, "libaacs.dll");
        var runtimeBdPlus = Path.Combine(runtimeDirectory, "libbdplus.dll");
        var aliasesStaged = FilesMatch(libMmbd, runtimeAacs) && FilesMatch(libMmbd, runtimeBdPlus);
        _discDecryptionProvider = aliasesStaged
            ? $"MakeMKV LibMMBD active via staged libaacs.dll/libbdplus.dll ({libMmbd})"
            : $"MakeMKV LibMMBD detected but runtime aliases are not staged ({libMmbd})";
    }

    private static bool FilesMatch(string left, string right)
    {
        try
        {
            if (!File.Exists(left) || !File.Exists(right)) return false;
            var leftInfo = new FileInfo(left);
            var rightInfo = new FileInfo(right);
            if (leftInfo.Length != rightInfo.Length) return false;
            return File.ReadAllBytes(left).AsSpan().SequenceEqual(File.ReadAllBytes(right));
        }
        catch
        {
            return false;
        }
    }

    public bool IsMakeMkvLibMmbdAvailable()
    {
        ConfigureDiscDecryptionProvider();
        return _discDecryptionProvider?.StartsWith("MakeMKV LibMMBD", StringComparison.Ordinal) == true;
    }

    public string DiscDecryptionProvider
    {
        get
        {
            ConfigureDiscDecryptionProvider();
            return _discDecryptionProvider ?? "plain libaacs/libbdplus fallback";
        }
    }

    private async Task LogNativeDiscLibrariesAsync(CancellationToken cancellationToken)
    {
        var baseDirectory = AppContext.BaseDirectory;
        foreach (var file in new[] { "libmpv-2.dll", "libaacs.dll", "libbdplus.dll", "libdvdcss-2.dll" })
        {
            var path = Path.Combine(baseDirectory, file);
            await logger.InfoAsync($"Native library {file}: {(File.Exists(path) ? path : "not present in output directory")}", cancellationToken)
                .ConfigureAwait(false);
        }

        var keyDb = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "aacs", "KEYDB.cfg");
        await logger.InfoAsync($"AACS key database: {(File.Exists(keyDb) ? keyDb : "not present")}", cancellationToken)
            .ConfigureAwait(false);
        await logger.InfoAsync($"Disc decryption provider: {DiscDecryptionProvider}", cancellationToken)
            .ConfigureAwait(false);
    }


    private static string CleanDiscTitle(string displayName, string driveRoot, string fallback = "Blu-ray Disc")
    {
        var title = displayName.Replace(driveRoot, string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        return string.IsNullOrWhiteSpace(title) ? fallback : title.Replace('_', ' ');
    }

    private static int ParseInt(string? value, int fallback = 0) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : fallback;

    private static double ParseDouble(string? value, double fallback = 0) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && double.IsFinite(result)
            ? result
            : fallback;

    private static string? BuildVideoDetails(int width, int height, double fps, string? gamma)
    {
        var parts = new List<string>();
        if (width > 0 && height > 0)
            parts.Add($"{width}×{height}");
        if (fps > 0)
            parts.Add($"{fps:0.###} fps");

        if (!string.IsNullOrWhiteSpace(gamma))
        {
            if (gamma.Contains("pq", StringComparison.OrdinalIgnoreCase))
                parts.Add("HDR (PQ)");
            else if (gamma.Contains("hlg", StringComparison.OrdinalIgnoreCase))
                parts.Add("HDR (HLG)");
        }

        return parts.Count == 0 ? null : string.Join(" • ", parts);
    }

    private static bool IsYes(string? value) =>
        string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) || value == "1" ||
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static TimeSpan ParseSeconds(string? value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            !double.IsFinite(seconds) || seconds < 0)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private async Task StopInternalPlaybackAsync(CancellationToken cancellationToken)
    {
        if (!IsInitialized) return;
        await WithInitializedContextAsync(context =>
        {
            try { _library.Command(context, "stop"); } catch { }
            _paused = false;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task StopDvdHostIfActiveAsync(CancellationToken cancellationToken)
    {
        // Release routing first: the poll loop stops querying the host before it is torn
        // down, and any in-flight failure belongs to a superseded generation.
        EndDvdHostRouting();
        if (_dvdHost is not null)
            await _dvdHost.StopHostAsync(cancellationToken).ConfigureAwait(false);
    }

    private long BeginDvdHostGeneration()
    {
        lock (_dvdHostSync)
        {
            _dvdHostGeneration++;
            _minimumDvdSessionId = (_dvdHost?.SessionId ?? 0) + 1;
            _usingDvdHost = true;
            return _dvdHostGeneration;
        }
    }

    private void EndDvdHostRouting()
    {
        lock (_dvdHostSync)
        {
            _dvdHostGeneration++;
            _usingDvdHost = false;
            _dvdHostReconciliationPending = false;
        }
    }

    /// <summary>
    /// Called when a DVD open fails. If this call had already claimed DVD routing, nothing
    /// may keep playing outside the service's control, so routing is released and any host
    /// (a previous one the start did not reach, or a partially started one) is stopped.
    /// If the failure happened before the claim, the existing playback is left untouched.
    /// </summary>
    private async Task AbandonDvdHostGenerationAsync(long generation, Exception cause)
    {
        if (generation < 0) return;

        bool owned;
        lock (_dvdHostSync)
        {
            owned = generation == _dvdHostGeneration;
            if (owned)
            {
                _dvdHostGeneration++;
                _usingDvdHost = false;
                _dvdHostReconciliationPending = false;
            }
        }

        if (!owned || _dvdHost is null) return;

        try
        {
            await _dvdHost.StopHostAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception cleanupEx) when (cleanupEx is AggregateException or TimeoutException)
        {
            await logger.ErrorAsync(
                $"DVD playback could not be started ({cause.GetType().Name}: {cause.Message}) and the DVD host could not be stopped cleanly.",
                cleanupEx,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private bool IsCurrentDvdHost(long generation, DvdPlaybackHostClient host, long sessionId) =>
        generation == Volatile.Read(ref _dvdHostGeneration) &&
        sessionId >= Volatile.Read(ref _minimumDvdSessionId) &&
        ReferenceEquals(_dvdHost, host) &&
        !host.IsTransitioning &&
        host.SessionId == sessionId;

    private async Task PollDvdHostAsync(CancellationToken cancellationToken)
    {
        var generation = Volatile.Read(ref _dvdHostGeneration);
        var host = _dvdHost;
        if (host is null) return;

        // A host being started, replaced, or stopped is not polled. A newly launched host
        // can be alive while its named pipe and libmpv context are still coming online;
        // keep the UI in Loading rather than falsely reporting a crash during that window.
        var status = host.GetStatus();
        if (host.IsTransitioning || status.Phase is DvdHostPhase.None or DvdHostPhase.Starting)
            return;

        // A session that predates the current routing claim is being replaced; it is
        // neither polled for state nor allowed to fault the new generation.
        if (!IsCurrentDvdHost(generation, host, status.SessionId))
            return;

        if (status.Phase == DvdHostPhase.Connected)
        {
            try
            {
                var hostSnapshot = await host.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
                if (!IsCurrentDvdHost(generation, host, status.SessionId))
                    return;

                if (_dvdHostReconciliationPending &&
                    hostSnapshot.State is PlaybackState.Playing or PlaybackState.Paused &&
                    hostSnapshot.Duration > TimeSpan.Zero)
                {
                    await ReconcileDvdHostTitleAsync(hostSnapshot, cancellationToken).ConfigureAwait(false);
                }

                var presentedSnapshot = ApplyDvdHostPresentation(hostSnapshot);
                lock (_dvdHostSync)
                {
                    if (generation == _dvdHostGeneration && presentedSnapshot != Snapshot)
                        SetSnapshot(presentedSnapshot);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (!IsCurrentDvdHost(generation, host, status.SessionId))
            {
                await logger.InfoAsync(
                    $"Ignored a polling failure from superseded DVD host session {status.SessionId}: {ex.GetType().Name}: {ex.Message}",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await FaultDvdHostAsync(generation, host, status.SessionId, ex.Message, "The isolated DVD playback host stopped responding.", ex, cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        var exitCode = status.ExitCode;
        var message = status.Phase == DvdHostPhase.Exited
            ? exitCode is int code
                ? $"The isolated DVD playback host exited with code 0x{unchecked((uint)code):X8}."
                : "The isolated DVD playback host exited unexpectedly."
            : $"The isolated DVD playback host stopped responding ({status.Reason ?? "connection lost"}).";
        await FaultDvdHostAsync(generation, host, status.SessionId, message, message, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Faults playback for <paramref name="sessionId"/> only if it is still the routed host
    /// of <paramref name="generation"/>, then reaps that session so a hung or broken host
    /// cannot keep running uncontrolled. A replacement session is never touched.
    /// </summary>
    private async Task FaultDvdHostAsync(
        long generation,
        DvdPlaybackHostClient host,
        long sessionId,
        string playbackError,
        string logMessage,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        bool faulted;
        lock (_dvdHostSync)
        {
            faulted = IsCurrentDvdHost(generation, host, sessionId);
            if (faulted)
            {
                LastPlaybackError = playbackError;
                _dvdHostReconciliationPending = false;
                _usingDvdHost = false;
                _dvdHostGeneration++;
                SetSnapshot(Snapshot with { State = PlaybackState.Faulted });
            }
        }

        if (!faulted)
        {
            await logger.InfoAsync(
                $"Ignored a failure from superseded DVD host session {sessionId}: {playbackError}",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await logger.ErrorAsync(logMessage, exception, cancellationToken).ConfigureAwait(false);

        try
        {
            await host.StopSessionAsync(sessionId, "faulted during playback polling").ConfigureAwait(false);
        }
        catch (Exception cleanupEx) when (cleanupEx is AggregateException or TimeoutException)
        {
            await logger.ErrorAsync(
                $"Faulted DVD host session {sessionId} could not be stopped cleanly.",
                cleanupEx,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Test seam: provides the video surface handle and starts the playback poll loop
    /// without loading libmpv, so DVD host routing can be exercised against a stub host.
    /// </summary>
    internal void StartPollingForTesting(nint videoWindowHandle)
    {
        _videoWindowHandle = videoWindowHandle;
        _dvdHost ??= _dvdHostFactory(logger);
        _pollTask ??= PollPlaybackAsync(_pollCancellation.Token);
    }

    private async Task WithInitializedContextAsync(Action<nint> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsInitialized) throw new InvalidOperationException("libmpv is not initialized yet.");
            action(_context);
        }
        catch (Exception ex)
        {
            SetSnapshot(Snapshot with { State = PlaybackState.Faulted });
            await logger.ErrorAsync("libmpv operation failed.", ex, cancellationToken).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void SetSnapshot(PlaybackSnapshot snapshot)
    {
        Snapshot = snapshot;
        StateChanged?.Invoke(this, new PlaybackStateChangedEventArgs(snapshot));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _pollCancellation.Cancel();
        if (_dvdHost is not null)
        {
            EndDvdHostRouting();
            try
            {
                await _dvdHost.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AggregateException or TimeoutException)
            {
                await logger.ErrorAsync("The isolated DVD playback host could not be stopped cleanly during shutdown.", ex, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            _dvdHost = null;
        }

        if (_pollTask is not null)
        {
            try { await _pollTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_context != nint.Zero)
            {
                // Stop audio/video immediately before destroying the client.
                // mpv_terminate_destroy is still the authoritative cleanup step.
                try { _library.Command(_context, "stop"); }
                catch { /* Destruction below must proceed even if stop fails. */ }

                _library.TerminateDestroy(_context);
                _context = nint.Zero;
            }
            _library.Dispose();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            _pollCancellation.Dispose();
        }
    }
}
