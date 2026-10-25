using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ParallaxStudio.App.Services;

public sealed record TmdbArtworkQuery(string Title, int? Year, string CacheIdentity)
{
    private static readonly Regex BracketedYear = new(
        @"[\[\(\{](?<year>19\d{2}|20\d{2})[\]\)\}]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex OpticalSuffix = new(
        @"\s+[—–-]\s+(?:Title\s+\d+|\d+\s+chapter\(s\).*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ReleaseSuffix = new(
        @"\s+\b(?:2160p|1080p|720p|480p|uhd|blu-?ray|bdrip|remux|web-?dl|webrip|hdtv|x264|x265|h\.?264|h\.?265|hevc|av1|hdr10\+?|hdr|dolby[ ._-]?vision|dv|truehd|dts(?:-hd)?|aac)\b.*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DvdTvBoxSet = new(
        @"^(?<title>.+?)\s+(?:COMPLETE\s+)?(?:SERIES|SEASON)\s+(?<year>19\d{2}|20\d{2})(?:\s+(?:(?:DISC|DISK)\s*\d+|D\s*\d+))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Punctuation = new(@"[^\p{L}\p{Nd}]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string NormalizedTitle => Normalize(Title);

    public string CacheKey
    {
        get
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(CacheIdentity));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }

    public static TmdbArtworkQuery? Create(string? source, string? playbackTitle)
    {
        var isOptical = IsOptical(source);
        var raw = playbackTitle?.Trim();

        if (string.IsNullOrWhiteSpace(raw) && !isOptical && IsLocalFile(source))
        {
            try { raw = Path.GetFileNameWithoutExtension(source); }
            catch (ArgumentException) { raw = null; }
        }

        if (string.IsNullOrWhiteSpace(raw)) return null;

        raw = raw.Replace('_', ' ').Replace('.', ' ');
        raw = OpticalSuffix.Replace(raw, string.Empty);

        int? year = null;
        var yearMatch = BracketedYear.Match(raw);
        if (yearMatch.Success && int.TryParse(yearMatch.Groups["year"].Value, out var parsedYear))
        {
            year = parsedYear;
            raw = BracketedYear.Replace(raw, " ");
        }

        raw = ReleaseSuffix.Replace(raw, string.Empty);
        raw = Whitespace.Replace(raw, " ").Trim(' ', '-', '–', '—', '.', '_');

        if (raw.Length < 2) return null;

        var normalized = Normalize(raw);
        if (normalized.Length < 2) return null;

        string identity;
        if (isOptical)
        {
            var kind = source?.StartsWith("Blu-ray disc", StringComparison.OrdinalIgnoreCase) == true ? "bluray" : "dvd";
            // Do not include D:\ / E:\ in optical identity. The poster belongs to the
            // disc/title, not the physical drive that happened to read it.
            identity = $"{kind}|{normalized}|{year?.ToString() ?? string.Empty}";
        }
        else
        {
            var pathIdentity = string.Empty;
            if (IsLocalFile(source))
            {
                try { pathIdentity = Path.GetFullPath(source!).ToUpperInvariant(); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
            }
            identity = $"file|{pathIdentity}|{normalized}|{year?.ToString() ?? string.Empty}";
        }

        return new TmdbArtworkQuery(raw, year, identity);
    }

    public static TmdbTvBoxSetHint? CreateDvdTvBoxSetHint(
        string? source,
        string? playbackTitle)
    {
        if (source?.StartsWith(
                "DVD disc",
                StringComparison.OrdinalIgnoreCase) != true)
        {
            return null;
        }

        var raw = playbackTitle?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        raw = raw.Replace('_', ' ').Replace('.', ' ');
        raw = OpticalSuffix.Replace(raw, string.Empty);
        raw = Whitespace.Replace(raw, " ")
            .Trim(' ', '-', '–', '—', '.', '_');

        var match = DvdTvBoxSet.Match(raw);
        if (!match.Success)
            return null;

        var title = Whitespace.Replace(
            match.Groups["title"].Value,
            " ").Trim();

        if (Normalize(title).Length < 2)
            return null;

        if (!int.TryParse(
                match.Groups["year"].Value,
                out var seasonYear))
        {
            return null;
        }

        return new TmdbTvBoxSetHint(
            title,
            seasonYear);
    }

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = Punctuation.Replace(value.Trim().ToLowerInvariant(), " ");
        return Whitespace.Replace(normalized, " ").Trim();
    }

    private static bool IsOptical(string? source) =>
        source?.StartsWith("Blu-ray disc", StringComparison.OrdinalIgnoreCase) == true ||
        source?.StartsWith("DVD disc", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsLocalFile(string? source)
    {
        if (string.IsNullOrWhiteSpace(source) || IsOptical(source)) return false;
        try { return Path.IsPathFullyQualified(source); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return false; }
    }
}

public sealed record TmdbTvBoxSetHint(string SearchTitle, int SeasonYear);

public sealed record TmdbArtworkCandidate(
    int Id,
    string MediaType,
    string Title,
    string? OriginalTitle,
    int? Year,
    string? PosterPath);

/// <summary>
/// Optional TMDB poster resolver. It never touches the playback backend or optical media.
/// Local artwork remains the first resolver in MainViewModel; this service only handles
/// Parallax-owned cache and online TMDB lookup.
/// </summary>
public sealed class TmdbArtworkService
{
    public const string TokenEnvironmentVariable = "PARALLAX_TMDB_TOKEN";
    private const long MaximumPosterBytes = 16 * 1024 * 1024;
    private const double RuntimeToleranceMinutes = 3.0;
    private const int MaximumDecodeDimension = 480;
    private const int MaximumSourceDimension = 32768;

    private static readonly HttpClient SharedHttpClient = CreateSharedClient();

    private readonly HttpClient _http;
    private readonly string? _token;
    private readonly string _cacheRoot;
    private TmdbImageConfiguration? _imageConfiguration;

    public TmdbArtworkService()
        : this(
            SharedHttpClient,
            Environment.GetEnvironmentVariable(TokenEnvironmentVariable),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ParallaxStudio", "cache", "artwork", "tmdb"))
    {
    }

    public TmdbArtworkService(HttpClient httpClient, string? token, string cacheRoot)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        _cacheRoot = string.IsNullOrWhiteSpace(cacheRoot)
            ? throw new ArgumentException("A cache root is required.", nameof(cacheRoot))
            : cacheRoot;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_token);

    public string ConfigurationStatus => IsConfigured
        ? $"TMDB ready via {TokenEnvironmentVariable}. Local artwork remains preferred."
        : $"TMDB is not configured. Set {TokenEnvironmentVariable}; cached posters can still be used.";

    public string GetCachePath(TmdbArtworkQuery query) =>
        Path.Combine(_cacheRoot, $"{query.CacheKey}.image");

    private string? GetRuntimeCachePath(
        TmdbArtworkQuery query,
        TimeSpan? playbackDuration)
    {
        if (playbackDuration is not { } duration || duration <= TimeSpan.Zero)
            return null;

        var runtimeMinutes = (int)Math.Round(
            duration.TotalMinutes,
            MidpointRounding.AwayFromZero);

        var identity = $"{query.CacheIdentity}|runtime:{runtimeMinutes}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));

        return Path.Combine(
            _cacheRoot,
            $"{Convert.ToHexString(hash).ToLowerInvariant()}.image");
    }

    public async Task<ImageSource?> LoadAsync(
        string? source,
        string? playbackTitle,
        bool onlineEnabled,
        bool cacheEnabled,
        CancellationToken cancellationToken = default,
        TimeSpan? playbackDuration = null)
    {
        if (!onlineEnabled) return null;

        var query = TmdbArtworkQuery.Create(source, playbackTitle);
        if (query is null) return null;

        var genericCachePath = GetCachePath(query);
        var runtimeCachePath = GetRuntimeCachePath(query, playbackDuration);

        if (cacheEnabled)
        {
            if (runtimeCachePath is not null)
            {
                var runtimeCached = await LoadCachedAsync(
                    runtimeCachePath,
                    cancellationToken).ConfigureAwait(false);

                if (runtimeCached is not null)
                    return runtimeCached;
            }

            var cached = await LoadCachedAsync(
                genericCachePath,
                cancellationToken).ConfigureAwait(false);

            if (cached is not null)
                return cached;
        }

        if (!IsConfigured) return null;

        try
        {
            var movieCandidates = await SearchAsync("movie", query, cancellationToken).ConfigureAwait(false);
            var movieMatches = EligibleExactCandidates(query, movieCandidates);
            var match = SelectBestCandidate(query, movieCandidates);
            var resolvedByRuntime = false;

            if (match is null && movieMatches.Length > 0)
            {
                match = await ResolveMovieByRuntimeAsync(
                    movieMatches,
                    playbackDuration,
                    cancellationToken).ConfigureAwait(false);

                resolvedByRuntime = match is not null;
            }


            // Movie candidates existed but could not be resolved uniquely.
            // Do not turn a movie ambiguity into an unrelated TV match.
            if (match is null && movieMatches.Length > 0)
                return null;

            if (match is null)
            {
                var tvCandidates = await SearchAsync("tv", query, cancellationToken).ConfigureAwait(false);
                var tvMatches = EligibleExactCandidates(query, tvCandidates);
                match = SelectBestCandidate(query, tvCandidates);
                if (match is null && tvMatches.Length == 0)
                {
                    match = await ResolveDvdTvBoxSetAsync(
                        source,
                        playbackTitle,
                        cancellationToken).ConfigureAwait(false);
                }

                if (match is null) return null;
            }

            if (string.IsNullOrWhiteSpace(match.PosterPath)) return null;

            var configuration = await GetImageConfigurationAsync(cancellationToken).ConfigureAwait(false);
            if (configuration is null) return null;

            var imageUrl = configuration.BuildPosterUrl(match.PosterPath);
            var bytes = await DownloadPosterAsync(imageUrl, cancellationToken).ConfigureAwait(false);
            if (bytes is null) return null;

            var image = await DecodeBytesAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (image is null) return null;

            if (cacheEnabled)
            {
                var selectedCachePath =
                    resolvedByRuntime && runtimeCachePath is not null
                        ? runtimeCachePath
                        : genericCachePath;

                await SaveCacheAsync(
                    selectedCachePath,
                    bytes,
                    cancellationToken).ConfigureAwait(false);
            }

            return image;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or
                                   InvalidDataException or UnauthorizedAccessException or
                                   NotSupportedException or ArgumentException)
        {
            // Online artwork is optional. Failure must not affect playback.
            return null;
        }
    }

    public static TmdbArtworkCandidate? SelectBestCandidate(
        TmdbArtworkQuery query,
        IReadOnlyList<TmdbArtworkCandidate> candidates)
    {
        var exact = EligibleExactCandidates(query, candidates);

        if (exact.Length == 0) return null;
        if (exact.Length == 1) return exact[0];

        // A localized TMDB title can collide with another movie's real title.
        // Prefer a unique exact match against original_title before declaring
        // the result ambiguous. Example:
        //
        //   They Live (1988) -> original_title = "They Live"
        //   Oni žive  (2018) -> localized title = "They Live"
        //
        // If more than one candidate still matches the original title, remain
        // conservative and return null rather than selecting the wrong poster.
        var originalTitleMatches = exact
            .Where(candidate =>
                !string.IsNullOrWhiteSpace(candidate.OriginalTitle) &&
                string.Equals(
                    TmdbArtworkQuery.Normalize(candidate.OriginalTitle),
                    query.NormalizedTitle,
                    StringComparison.Ordinal))
            .ToArray();

        return originalTitleMatches.Length == 1
            ? originalTitleMatches[0]
            : null;
    }

    private static TmdbArtworkCandidate[] EligibleExactCandidates(
        TmdbArtworkQuery query,
        IReadOnlyList<TmdbArtworkCandidate> candidates)
    {
        var exact = candidates
            .Where(candidate =>
                !string.IsNullOrWhiteSpace(candidate.PosterPath) &&
                CandidateTitles(candidate).Any(title =>
                    string.Equals(TmdbArtworkQuery.Normalize(title), query.NormalizedTitle, StringComparison.Ordinal)));

        if (query.Year is int requestedYear)
            exact = exact.Where(candidate => candidate.Year == requestedYear);

        return exact.ToArray();
    }

    private async Task<TmdbArtworkCandidate?> ResolveMovieByRuntimeAsync(
        IReadOnlyList<TmdbArtworkCandidate> candidates,
        TimeSpan? playbackDuration,
        CancellationToken cancellationToken)
    {
        if (playbackDuration is not { } duration || duration <= TimeSpan.Zero)
            return null;

        // Keep detail lookup bounded. Extremely broad title collisions remain
        // unresolved rather than producing a large set of HTTP requests.
        if (candidates.Count is 0 or > 8)
            return null;

        var matches = new List<TmdbArtworkCandidate>();

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var runtimeMinutes = await GetMovieRuntimeMinutesAsync(
                candidate.Id,
                cancellationToken).ConfigureAwait(false);

            if (runtimeMinutes is not int minutes || minutes <= 0)
                continue;

            if (Math.Abs(minutes - duration.TotalMinutes) <= RuntimeToleranceMinutes)
                matches.Add(candidate);
        }

        return matches.Count == 1
            ? matches[0]
            : null;
    }

    private async Task<int?> GetMovieRuntimeMinutesAsync(
        int movieId,
        CancellationToken cancellationToken)
    {
        var url =
            $"https://api.themoviedb.org/3/movie/{movieId}?language=en-US";

        using var response = await SendApiAsync(
            url,
            cancellationToken).ConfigureAwait(false);

        if (response is null || !response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return TryGetInt(document.RootElement, "runtime");
    }

    private async Task<TmdbArtworkCandidate?> ResolveDvdTvBoxSetAsync(
        string? source,
        string? playbackTitle,
        CancellationToken cancellationToken)
    {
        var hint = TmdbArtworkQuery.CreateDvdTvBoxSetHint(
            source,
            playbackTitle);

        if (hint is null)
            return null;

        // Do not pass the packaging year as first_air_date_year.
        // For box sets such as "COMPLETE SERIES 2018", it describes
        // season/content context rather than the show's premiere year.
        var hintQuery = new TmdbArtworkQuery(
            hint.SearchTitle,
            null,
            $"dvd-tv-hint|{TmdbArtworkQuery.Normalize(hint.SearchTitle)}|{hint.SeasonYear}");

        var candidates = await SearchAsync(
            "tv",
            hintQuery,
            cancellationToken).ConfigureAwait(false);

        var normalizedHint = NormalizeTvBoxTitle(
            hint.SearchTitle);

        if (string.IsNullOrWhiteSpace(normalizedHint))
            return null;

        // Alias normalization is deliberately narrow. "Dr." -> "Doctor"
        // is token-level language normalization, not a Doctor Who special case.
        var exactTitleCandidates = candidates
            .Where(candidate =>
                !string.IsNullOrWhiteSpace(candidate.PosterPath) &&
                CandidateTitles(candidate).Any(title =>
                    string.Equals(
                        NormalizeTvBoxTitle(title),
                        normalizedHint,
                        StringComparison.Ordinal)))
            .Take(9)
            .ToArray();

        if (exactTitleCandidates.Length is 0 or > 8)
            return null;

        var seasonMatches = new List<TmdbArtworkCandidate>();

        foreach (var candidate in exactTitleCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await TvCandidateHasSeasonYearAsync(
                    candidate.Id,
                    hint.SeasonYear,
                    cancellationToken).ConfigureAwait(false))
            {
                seasonMatches.Add(candidate);
            }
        }

        return seasonMatches.Count == 1
            ? seasonMatches[0]
            : null;
    }

    private static string NormalizeTvBoxTitle(string? value)
    {
        var normalized = TmdbArtworkQuery.Normalize(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        var tokens = normalized.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index < tokens.Length; index++)
        {
            if (string.Equals(
                    tokens[index],
                    "dr",
                    StringComparison.Ordinal))
            {
                tokens[index] = "doctor";
            }
        }

        return string.Join(' ', tokens);
    }

    private async Task<bool> TvCandidateHasSeasonYearAsync(
        int tvId,
        int seasonYear,
        CancellationToken cancellationToken)
    {
        var url =
            $"https://api.themoviedb.org/3/tv/{tvId}?language=en-US";

        using var response = await SendApiAsync(
            url,
            cancellationToken).ConfigureAwait(false);

        if (response is null || !response.IsSuccessStatusCode)
            return false;

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty(
                "seasons",
                out var seasons) ||
            seasons.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var season in seasons.EnumerateArray())
        {
            var airDate = TryGetString(
                season,
                "air_date");

            if (ParseYear(airDate) == seasonYear)
                return true;
        }

        return false;
    }

    private async Task<IReadOnlyList<TmdbArtworkCandidate>> SearchAsync(
        string mediaType,
        TmdbArtworkQuery query,
        CancellationToken cancellationToken)
    {
        var encodedTitle = Uri.EscapeDataString(query.Title);
        var yearParameter = query.Year is int year
            ? mediaType == "movie" ? $"&year={year}" : $"&first_air_date_year={year}"
            : string.Empty;
        var url = $"https://api.themoviedb.org/3/search/{mediaType}?query={encodedTitle}&include_adult=false&language=en-US{yearParameter}";

        using var response = await SendApiAsync(url, cancellationToken).ConfigureAwait(false);
        if (response is null || !response.IsSuccessStatusCode) return [];

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return [];

        var candidates = new List<TmdbArtworkCandidate>();
        foreach (var item in results.EnumerateArray().Take(20))
        {
            var id = TryGetInt(item, "id");
            if (id is null) continue;

            var title = mediaType == "movie"
                ? TryGetString(item, "title")
                : TryGetString(item, "name");
            if (string.IsNullOrWhiteSpace(title)) continue;

            var original = mediaType == "movie"
                ? TryGetString(item, "original_title")
                : TryGetString(item, "original_name");
            var date = mediaType == "movie"
                ? TryGetString(item, "release_date")
                : TryGetString(item, "first_air_date");

            candidates.Add(new TmdbArtworkCandidate(
                id.Value,
                mediaType,
                title,
                original,
                ParseYear(date),
                TryGetString(item, "poster_path")));
        }

        return candidates;
    }

    private async Task<TmdbImageConfiguration?> GetImageConfigurationAsync(CancellationToken cancellationToken)
    {
        if (_imageConfiguration is not null) return _imageConfiguration;

        using var response = await SendApiAsync("https://api.themoviedb.org/3/configuration", cancellationToken)
            .ConfigureAwait(false);
        if (response is null || !response.IsSuccessStatusCode) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("images", out var images)) return null;
        var baseUrl = TryGetString(images, "secure_base_url");
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;

        var sizes = new List<string>();
        if (images.TryGetProperty("poster_sizes", out var posterSizes) && posterSizes.ValueKind == JsonValueKind.Array)
        {
            foreach (var size in posterSizes.EnumerateArray())
                if (size.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(size.GetString()))
                    sizes.Add(size.GetString()!);
        }

        var preferred = sizes.Contains("w500", StringComparer.OrdinalIgnoreCase)
            ? "w500"
            : sizes
                .Select(size => new { Size = size, Width = ParseWidth(size) })
                .Where(item => item.Width is > 0 and <= 500)
                .OrderByDescending(item => item.Width)
                .Select(item => item.Size)
                .FirstOrDefault()
              ?? sizes.FirstOrDefault(size => size.StartsWith("w", StringComparison.OrdinalIgnoreCase))
              ?? "original";

        _imageConfiguration = new TmdbImageConfiguration(baseUrl, preferred);
        return _imageConfiguration;
    }

    private async Task<HttpResponseMessage?> SendApiAsync(string url, CancellationToken cancellationToken)
    {
        if (!IsConfigured) return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<byte[]?> DownloadPosterAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        if (response.Content.Headers.ContentLength is long declaredLength &&
            (declaredLength <= 0 || declaredLength > MaximumPosterBytes))
            return null;

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > MaximumPosterBytes) return null;
            output.Write(buffer, 0, read);
        }

        return total > 0 ? output.ToArray() : null;
    }

    private static async Task<ImageSource?> LoadCachedAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaximumPosterBytes)
            {
                TryDelete(path);
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var image = await DecodeBytesAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (image is null) TryDelete(path);
            return image;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static Task<ImageSource?> DecodeBytesAsync(byte[] bytes, CancellationToken cancellationToken) =>
        Task.Run<ImageSource?>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                if (decoder.Frames.Count == 0) return null;

                var frame = decoder.Frames[0];
                var width = frame.PixelWidth;
                var height = frame.PixelHeight;
                if (width <= 0 || height <= 0 ||
                    width > MaximumSourceDimension || height > MaximumSourceDimension ||
                    (long)width * height > 100_000_000)
                    return null;

                stream.Position = 0;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                if (width >= height) image.DecodePixelWidth = Math.Min(width, MaximumDecodeDimension);
                else image.DecodePixelHeight = Math.Min(height, MaximumDecodeDimension);
                image.EndInit();
                image.Freeze();
                cancellationToken.ThrowIfCancellationRequested();
                return image;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException)
            {
                return null;
            }
        }, cancellationToken);

    private static async Task SaveCacheAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory)) return;
            Directory.CreateDirectory(directory);

            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                TryDelete(temporary);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Cache failure is non-fatal; the already decoded poster can still be shown.
        }
    }

    private static IEnumerable<string> CandidateTitles(TmdbArtworkCandidate candidate)
    {
        yield return candidate.Title;
        if (!string.IsNullOrWhiteSpace(candidate.OriginalTitle) &&
            !string.Equals(candidate.OriginalTitle, candidate.Title, StringComparison.OrdinalIgnoreCase))
            yield return candidate.OriginalTitle;
    }

    private static int? ParseYear(string? date)
    {
        if (string.IsNullOrWhiteSpace(date) || date.Length < 4) return null;
        return int.TryParse(date.AsSpan(0, 4), out var year) ? year : null;
    }

    private static int ParseWidth(string size) =>
        size.Length > 1 && (size[0] == 'w' || size[0] == 'W') &&
        int.TryParse(size.AsSpan(1), out var width)
            ? width
            : 0;

    private static string? TryGetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? TryGetInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }

    private static HttpClient CreateSharedClient() => new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    private sealed record TmdbImageConfiguration(string BaseUrl, string PosterSize)
    {
        public string BuildPosterUrl(string posterPath) =>
            $"{BaseUrl.TrimEnd('/')}/{PosterSize}/{posterPath.TrimStart('/')}";
    }
}
