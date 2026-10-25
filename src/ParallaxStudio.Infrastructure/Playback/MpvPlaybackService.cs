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
    private string? _activeDvdDevice;
    private int _activeDvdTitleIndex = -1;
    private string? _loadedDvdCacheKey;
    private readonly Dictionary<string, IReadOnlyList<BluRayTitle>> _dvdTitleCache = new(StringComparer.OrdinalIgnoreCase);
    private int _pendingDvdEditionIndex = -1;
    private bool _pendingDvdEditionSwitchIssued;
    private readonly HashSet<string> _dvdProbePassCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dvdFullMapCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<int, int>> _dvdPlaybackTitleByMetadataIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _dvdMappingSync = new();
    private DvdPlaybackHostClient? _dvdHost;
    private bool _usingDvdHost;
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
            _dvdHost ??= new DvdPlaybackHostClient(logger);
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
            _loadedBluRayCacheKey = BuildDiscCacheKey(disc);
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
            _activeBluRayDevice = device;
            _loadedBluRayCacheKey = BuildDiscCacheKey(disc);
            _activeBluRayTitleIndex = titleIndex;
            LastPlaybackError = null;
            await WithInitializedContextAsync(context =>
            {
                SetSnapshot(Snapshot with
                {
                    State = PlaybackState.Loading,
                    Source = $"Blu-ray disc • {device} • title {titleIndex + 1}",
                    Title = $"{CleanDiscTitle(disc.DisplayName, device)} — Title {titleIndex + 1}",
                    Position = TimeSpan.Zero,
                    Duration = TimeSpan.Zero
                });

                _library.SetProperty(context, "bluray-device", device);
                _library.Command(context, "loadfile", $"bd://{titleIndex}", "replace");
                _paused = false;
            }, cancellationToken).ConfigureAwait(false);

            await logger.InfoAsync($"Submitted Blu-ray title {titleIndex + 1} request for {device}.", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _discDiscoveryGate.Release();
        }
    }

    public async Task<IReadOnlyList<BluRayTitle>> GetBluRayTitlesAsync(DiscSource disc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disc);
        if (disc.Kind != DiscKind.BluRay || !disc.IsReady)
            return [];

        var device = Path.GetPathRoot(disc.DriveRoot) ?? disc.DriveRoot;
        var cacheKey = BuildDiscCacheKey(disc);
        var wasCurrentDisc = string.Equals(_loadedBluRayCacheKey, cacheKey, StringComparison.OrdinalIgnoreCase);
        _activeBluRayDevice = device;

        var active = wasCurrentDisc ? _activeBluRayTitleIndex : -1;
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

        if (active < 0 && wasCurrentDisc &&
            Snapshot.Source?.StartsWith("Blu-ray disc", StringComparison.OrdinalIgnoreCase) == true)
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
        try
        {
            var device = Path.GetPathRoot(disc.DriveRoot) ?? disc.DriveRoot;
            _activeDvdDevice = device;
            _activeDvdDisplayName = CleanDiscTitle(disc.DisplayName, device, "DVD Disc");
            _loadedDvdCacheKey = BuildDiscCacheKey(disc);
            _activeDvdTitleIndex = _dvdTitleCache.TryGetValue(_loadedDvdCacheKey, out var cachedDvdTitles) && cachedDvdTitles.Count > 0
                ? cachedDvdTitles.OrderByDescending(title => title.Duration).First().Index
                : -1;
            _requestedDvdMetadataIndex = _activeDvdTitleIndex;
            _activeDvdPlaybackTitleNumber = -1;
            _dvdHostReconciliationPending = true;
            LastPlaybackError = null;

            await StopInternalPlaybackAsync(cancellationToken).ConfigureAwait(false);
            SetSnapshot(Snapshot with
            {
                State = PlaybackState.Loading,
                Source = $"DVD disc • {device}",
                Title = CleanDiscTitle(disc.DisplayName, device, "DVD Disc"),
                Position = TimeSpan.Zero,
                Duration = TimeSpan.Zero
            });

            _dvdHost ??= new DvdPlaybackHostClient(logger);
            _usingDvdHost = true;
            await _dvdHost.StartAsync(
                _videoWindowHandle,
                disc,
                titleNumber: null,
                Snapshot.Volume,
                Snapshot.IsMuted,
                cancellationToken).ConfigureAwait(false);
            await logger.InfoAsync($"Started isolated DVD playback host for the longest title on {device}.", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _dvdHostReconciliationPending = false;
            _usingDvdHost = false;
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
        try
        {
            var device = Path.GetPathRoot(disc.DriveRoot) ?? disc.DriveRoot;
            _activeDvdDevice = device;
            _activeDvdDisplayName = CleanDiscTitle(disc.DisplayName, device, "DVD Disc");
            _loadedDvdCacheKey = BuildDiscCacheKey(disc);

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
                    throw new InvalidOperationException(
                        $"Parallax could not confidently match DVD metadata title {titleIndex + 1} to a playable DVD title. " +
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
            SetSnapshot(Snapshot with
            {
                State = PlaybackState.Loading,
                Source = $"DVD disc • {device} • title {dvdTitleNumber + 1}",
                Title = $"{CleanDiscTitle(disc.DisplayName, device, "DVD Disc")} — Title {dvdTitleNumber + 1}",
                Position = TimeSpan.Zero,
                Duration = TimeSpan.Zero
            });

            _dvdHost ??= new DvdPlaybackHostClient(logger);
            _usingDvdHost = true;
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
        catch
        {
            _dvdHostReconciliationPending = false;
            _usingDvdHost = false;
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
        var cacheKey = BuildDiscCacheKey(disc);
        var wasCurrentDisc = string.Equals(_loadedDvdCacheKey, cacheKey, StringComparison.OrdinalIgnoreCase);
        _activeDvdDevice = device;

        var active = wasCurrentDisc ? _activeDvdTitleIndex : -1;
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

        if (active < 0 && wasCurrentDisc &&
            Snapshot.Source?.StartsWith("DVD disc", StringComparison.OrdinalIgnoreCase) == true)
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
        var cacheKey = BuildDiscCacheKey(disc);
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

        var editions = ParseDvdEditionMap(stdout);
        var mapping = BuildDvdMetadataToSelectorMap(metadataTitles, editions);
        if (mapping.Count == 0)
            throw new InvalidOperationException("The DVD exposed no title durations that could be matched confidently to the discovered metadata.");

        lock (_dvdMappingSync)
        {
            _dvdPlaybackTitleByMetadataIndex[cacheKey] = mapping;
            _dvdFullMapCache.Add(cacheKey);
        }

        var mappedText = string.Join(", ", mapping
            .OrderBy(pair => pair.Key)
            .Select(pair => $"metadata {pair.Key + 1}->DVD title {pair.Value + 1}"));
        await logger.InfoAsync($"Full DVD title map built with {mapping.Count} confident matches: {mappedText}.", cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<DvdEditionInfo> ParseDvdEditionMap(string output)
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

    private static Dictionary<int, int> BuildDvdMetadataToSelectorMap(
        IReadOnlyList<BluRayTitle> metadataTitles,
        IReadOnlyList<DvdEditionInfo> editions)
    {
        var candidates = metadataTitles
            .Where(title => title.Duration > TimeSpan.Zero && title.Duration <= TimeSpan.FromHours(12))
            .SelectMany(title => editions
                .Where(edition => edition.Selector >= 0 && edition.Duration > TimeSpan.Zero && edition.Duration <= TimeSpan.FromHours(12))
                .Select(edition => new
                {
                    MetadataIndex = title.Index,
                    edition.Selector,
                    Delta = Math.Abs((title.Duration - edition.Duration).TotalSeconds),
                    Tolerance = Math.Max(20d, title.Duration.TotalSeconds * 0.025d)
                }))
            .Where(candidate => candidate.Delta <= candidate.Tolerance)
            .OrderBy(candidate => candidate.Delta)
            .ThenBy(candidate => candidate.MetadataIndex)
            .ThenBy(candidate => candidate.Selector)
            .ToArray();

        var result = new Dictionary<int, int>();
        var usedSelectors = new HashSet<int>();
        foreach (var candidate in candidates)
        {
            if (result.ContainsKey(candidate.MetadataIndex) || usedSelectors.Contains(candidate.Selector))
                continue;
            result[candidate.MetadataIndex] = candidate.Selector;
            usedSelectors.Add(candidate.Selector);
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
        var match = FindBestDvdMetadataMatch(metadataTitles, actualDuration, actualChapterCount);
        if (match is null)
        {
            await logger.WarningAsync(
                $"DVD title reconciliation found no confident metadata match for DVD selector {_activeDvdPlaybackTitleNumber} (title {_activeDvdPlaybackTitleNumber + 1}): " +
                $"runtime={FormatDuration(actualDuration)}, chapters={actualChapterCount}.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var previousMetadataIndex = _requestedDvdMetadataIndex;
        _activeDvdTitleIndex = match.Index;

        if (_activeDvdPlaybackTitleNumber >= 0)
        {
            lock (_dvdMappingSync)
            {
                if (!_dvdPlaybackTitleByMetadataIndex.TryGetValue(_loadedDvdCacheKey, out var mapping))
                {
                    mapping = new Dictionary<int, int>();
                    _dvdPlaybackTitleByMetadataIndex[_loadedDvdCacheKey] = mapping;
                }

                foreach (var staleKey in mapping
                    .Where(pair => pair.Value == _activeDvdPlaybackTitleNumber && pair.Key != match.Index)
                    .Select(pair => pair.Key)
                    .ToArray())
                {
                    mapping.Remove(staleKey);
                }

                mapping[match.Index] = _activeDvdPlaybackTitleNumber;
            }
        }

        var requestedText = previousMetadataIndex >= 0 ? (previousMetadataIndex + 1).ToString(CultureInfo.InvariantCulture) : "longest";
        await logger.InfoAsync(
            $"DVD title reconciled: requested metadata title {requestedText}, DVD selector {_activeDvdPlaybackTitleNumber} (title {_activeDvdPlaybackTitleNumber + 1}), " +
            $"actual runtime={FormatDuration(actualDuration)}, chapters={actualChapterCount}, " +
            $"matched metadata title {match.DisplayNumber} ({match.DurationText}, {match.ChapterCount} chapters).",
            cancellationToken).ConfigureAwait(false);
    }

    private static BluRayTitle? FindBestDvdMetadataMatch(
        IReadOnlyList<BluRayTitle> metadataTitles,
        TimeSpan actualDuration,
        int actualChapterCount)
    {
        if (actualDuration <= TimeSpan.Zero)
            return null;

        var ranked = metadataTitles
            .Where(title => title.Duration > TimeSpan.Zero && title.Duration <= TimeSpan.FromHours(12))
            .Select(title => new
            {
                Title = title,
                DurationDelta = Math.Abs((title.Duration - actualDuration).TotalSeconds),
                ChapterDelta = actualChapterCount > 0 && title.ChapterCount > 0
                    ? Math.Abs(title.ChapterCount - actualChapterCount)
                    : 0
            })
            .Select(item => new
            {
                item.Title,
                item.DurationDelta,
                item.ChapterDelta,
                Score = item.DurationDelta + (item.ChapterDelta * 180d)
            })
            .OrderBy(item => item.Score)
            .ThenBy(item => item.DurationDelta)
            .ThenBy(item => item.Title.Index)
            .ToArray();

        if (ranked.Length == 0)
            return null;

        var best = ranked[0];
        var durationTolerance = Math.Max(20d, actualDuration.TotalSeconds * 0.025d);
        if (best.DurationDelta > durationTolerance)
            return null;
        if (actualChapterCount > 0 && best.Title.ChapterCount > 0 && best.ChapterDelta > 1)
            return null;

        if (ranked.Length > 1)
        {
            var second = ranked[1];
            var ambiguous = Math.Abs(second.Score - best.Score) < 3d &&
                Math.Abs(second.DurationDelta - best.DurationDelta) < 3d;
            if (ambiguous)
                return null;
        }

        return best.Title;
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

    private static string BuildDiscCacheKey(DiscSource disc)
    {
        var root = Path.GetPathRoot(disc.DriveRoot) ?? disc.DriveRoot;
        var identityParts = new List<string>
        {
            root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            disc.DisplayName.Trim(),
            disc.Kind.ToString()
        };

        try
        {
            var identityFile = disc.Kind == DiscKind.Dvd
                ? Path.Combine(root, "VIDEO_TS", "VIDEO_TS.IFO")
                : Path.Combine(root, "BDMV", "index.bdmv");
            if (File.Exists(identityFile))
            {
                var info = new FileInfo(identityFile);
                identityParts.Add(info.Length.ToString(CultureInfo.InvariantCulture));
                identityParts.Add(info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            }
        }
        catch
        {
            // Volume label and drive remain a safe fallback identity.
        }

        return string.Join("|", identityParts).ToUpperInvariant();
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

        await WithInitializedContextAsync(context =>
        {
            _paused = !_paused;
            _library.SetProperty(context, "pause", _paused ? "yes" : "no");
            SetSnapshot(Snapshot with { State = _paused ? PlaybackState.Paused : PlaybackState.Playing });
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_usingDvdHost && _dvdHost is { IsActive: true })
        {
            await _dvdHost.StopAsync(cancellationToken).ConfigureAwait(false);
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
                    if (_dvdHost is { IsActive: true })
                    {
                        try
                        {
                            var hostSnapshot = await _dvdHost.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
                            if (_dvdHostReconciliationPending &&
                                hostSnapshot.State is PlaybackState.Playing or PlaybackState.Paused &&
                                hostSnapshot.Duration > TimeSpan.Zero)
                            {
                                await ReconcileDvdHostTitleAsync(hostSnapshot, cancellationToken).ConfigureAwait(false);
                            }

                            var presentedSnapshot = ApplyDvdHostPresentation(hostSnapshot);
                            if (presentedSnapshot != Snapshot) SetSnapshot(presentedSnapshot);
                        }
                        catch (Exception ex)
                        {
                            LastPlaybackError = ex.Message;
                            _dvdHostReconciliationPending = false;
                            _usingDvdHost = false;
                            SetSnapshot(Snapshot with { State = PlaybackState.Faulted });
                            await logger.ErrorAsync("The isolated DVD playback host stopped responding.", ex, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        // A newly launched host can be alive while its named pipe and
                        // libmpv context are still coming online. Keep the UI in Loading
                        // rather than falsely reporting a crash during that window.
                        if (_dvdHost is not null && _dvdHost.ExitCode is null)
                            continue;

                        var exitCode = _dvdHost?.ExitCode;
                        LastPlaybackError = exitCode is int code
                            ? $"The isolated DVD playback host exited with code 0x{unchecked((uint)code):X8}."
                            : "The isolated DVD playback host exited unexpectedly.";
                        _dvdHostReconciliationPending = false;
                        _usingDvdHost = false;
                        SetSnapshot(Snapshot with { State = PlaybackState.Faulted });
                        await logger.ErrorAsync(LastPlaybackError ?? "The isolated DVD playback host exited unexpectedly.", cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
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

                    if (failed is not null && _pendingDvdEditionIndex >= 0)
                    {
                        _pendingDvdEditionIndex = -1;
                        _pendingDvdEditionSwitchIssued = false;
                    }

                    if (fileLoaded && _pendingDvdEditionIndex >= 0)
                    {
                        if (!_pendingDvdEditionSwitchIssued)
                        {
                            var pendingEdition = _pendingDvdEditionIndex;
                            _library.SetProperty(_context, "edition", pendingEdition.ToString(CultureInfo.InvariantCulture));
                            _pendingDvdEditionSwitchIssued = true;
                            diagnosticEvents.Add(new MpvDiagnosticEvent(
                                "dvd-edition",
                                "info",
                                "player",
                                $"Base DVD load completed. Switching to DVD title {pendingEdition + 1} through edition={pendingEdition}."));
                        }
                        else
                        {
                            var selectedEdition = ParseInt(_library.GetProperty(_context, "current-edition"), -1);
                            diagnosticEvents.Add(new MpvDiagnosticEvent(
                                "dvd-edition",
                                "info",
                                "player",
                                $"DVD title switch completed. Requested edition={_pendingDvdEditionIndex}, current-edition={selectedEdition}."));
                            _activeDvdTitleIndex = _pendingDvdEditionIndex;
                            _pendingDvdEditionIndex = -1;
                            _pendingDvdEditionSwitchIssued = false;
                        }
                    }

                    var position = ParseSeconds(_library.GetProperty(_context, "time-pos"));
                    var duration = ParseSeconds(_library.GetProperty(_context, "duration"));
                    var pause = _library.GetProperty(_context, "pause");
                    var idle = _library.GetProperty(_context, "idle-active");
                    var mute = _library.GetProperty(_context, "mute");

                    _paused = string.Equals(pause, "yes", StringComparison.OrdinalIgnoreCase);
                    var state = Snapshot.State;
                    var dvdTitleTransitionPending = _pendingDvdEditionIndex >= 0;
                    if (!string.IsNullOrWhiteSpace(Snapshot.Source))
                    {
                        if (failed is not null)
                        {
                            state = PlaybackState.Faulted;
                        }
                        else if (Snapshot.State == PlaybackState.Loading && (!fileLoaded || dvdTitleTransitionPending))
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
                        Position = position,
                        Duration = duration,
                        IsMuted = string.Equals(mute, "yes", StringComparison.OrdinalIgnoreCase)
                    };

                    if (next != Snapshot) SetSnapshot(next);
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

    private async Task EnsureDvdRuntimeProbeAsync(DiscSource disc, int? titleNumber, CancellationToken cancellationToken)
    {
        var cacheKey = $"{BuildDiscCacheKey(disc)}|{(titleNumber is > 0 ? titleNumber.Value.ToString(CultureInfo.InvariantCulture) : "longest")}";
        if (_dvdProbePassCache.Contains(cacheKey))
            return;

        var probePath = Path.Combine(AppContext.BaseDirectory, "dvd-probe", "ParallaxStudio.DvdProbe.exe");
        if (!File.Exists(probePath))
            throw new InvalidOperationException($"DVD safety probe is missing: {probePath}");

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
        if (titleNumber is > 0)
            startInfo.ArgumentList.Add(titleNumber.Value.ToString(CultureInfo.InvariantCulture));

        await logger.InfoAsync(
            $"Starting isolated DVD runtime probe for {(titleNumber is > 0 ? $"title {titleNumber}" : "longest title")} on {device}.",
            cancellationToken).ConfigureAwait(false);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException("The DVD safety probe process could not be started.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            var exitTask = process.WaitForExitAsync(cancellationToken);
            var completed = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(60), cancellationToken)).ConfigureAwait(false);
            if (!ReferenceEquals(completed, exitTask))
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException("The isolated DVD runtime probe did not finish within 60 seconds.");
            }
            await exitTask.ConfigureAwait(false);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        var stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
        var stderr = (await stderrTask.ConfigureAwait(false)).Trim();
        if (!string.IsNullOrWhiteSpace(stdout))
            await logger.InfoAsync($"DVD probe output: {stdout}", cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(stderr))
            await logger.WarningAsync($"DVD probe error output: {stderr}", cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var unsignedExit = unchecked((uint)process.ExitCode);
            throw new InvalidOperationException(
                $"DVD playback was blocked because the isolated native probe failed (exit 0x{unsignedExit:X8}). " +
                "Parallax Studio remained open; check the playback log for the probe output.");
        }

        _dvdProbePassCache.Add(cacheKey);
        await logger.InfoAsync("DVD runtime safety probe passed.", cancellationToken).ConfigureAwait(false);
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
        if (_dvdHost is not null)
            await _dvdHost.StopHostAsync(cancellationToken).ConfigureAwait(false);
        _dvdHostReconciliationPending = false;
        _usingDvdHost = false;
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
            try { await _dvdHost.DisposeAsync().ConfigureAwait(false); } catch { }
            _dvdHost = null;
            _usingDvdHost = false;
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
