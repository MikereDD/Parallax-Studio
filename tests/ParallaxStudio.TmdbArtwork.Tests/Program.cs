using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using ParallaxStudio.App.Services;

namespace ParallaxStudio.TmdbArtwork.Tests;

internal static class Program
{
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFElEQVR4nGP8z8Dwn4GBgYGJAQoAHxcCAk+Uzr4AAAAASUVORK5CYII=");

    [STAThread]
    private static async Task<int> Main()
    {
        var checks = new (string Name, Func<Task> Run)[]
        {
            ("disabled setting avoids network", DisabledAvoidsNetworkAsync),
            ("missing token avoids network", MissingTokenAvoidsNetworkAsync),
            ("cache hit works without token", CacheHitWorksWithoutTokenAsync),
            ("exact title and year selects poster", ExactTitleYearAsync),
            ("movie match wins before TV fallback", MovieWinsBeforeTvAsync),
            ("localized title collision prefers exact original title", LocalizedTitleCollisionPrefersOriginalAsync),
            ("runtime disambiguates duplicate exact movie titles", RuntimeDisambiguatesMovieAsync),
            ("ambiguous title-only lookup is refused", AmbiguousTitleRefusedAsync),
            ("network failure degrades cleanly", NetworkFailureAsync),
            ("corrupt poster is rejected", CorruptPosterRejectedAsync),
            ("oversized poster is rejected", OversizedPosterRejectedAsync),
            ("cancellation is honored", CancellationAsync),
            ("optical metadata branding is stripped", OpticalMetadataBrandingAsync),
            ("optical cache identity ignores drive letter", OpticalIdentityAsync)
        };

        var failed = 0;
        foreach (var check in checks)
        {
            try
            {
                await check.Run();
                Console.WriteLine($"PASS: {check.Name}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"FAIL: {check.Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"{checks.Length - failed}/{checks.Length} TMDB artwork checks passed.");
        return failed == 0 ? 0 : 1;
    }

    private static async Task DisabledAvoidsNetworkAsync()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHandler(_ => throw new InvalidOperationException("HTTP should not be called."));
        using var http = new HttpClient(handler);
        var service = new TmdbArtworkService(http, "token", temp.Path);
        var result = await service.LoadAsync(@"C:\Movies\Arrival.mkv", "Arrival", false, true);
        Expect(result is null, "disabled online artwork should return null");
        Expect(handler.Calls == 0, "disabled online artwork should not issue HTTP");
    }

    private static async Task MissingTokenAvoidsNetworkAsync()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHandler(_ => throw new InvalidOperationException("HTTP should not be called."));
        using var http = new HttpClient(handler);
        var service = new TmdbArtworkService(http, null, temp.Path);
        var result = await service.LoadAsync(@"C:\Movies\Arrival.mkv", "Arrival", true, true);
        Expect(result is null, "missing token should return null without cache");
        Expect(handler.Calls == 0, "missing token should not issue HTTP");
    }

    private static async Task CacheHitWorksWithoutTokenAsync()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHandler(_ => throw new InvalidOperationException("HTTP should not be called for cache hit."));
        using var http = new HttpClient(handler);
        var service = new TmdbArtworkService(http, null, temp.Path);
        var query = TmdbArtworkQuery.Create(@"C:\Movies\Arrival.mkv", "Arrival")!;
        Directory.CreateDirectory(temp.Path);
        await File.WriteAllBytesAsync(service.GetCachePath(query), TinyPng);

        var result = await service.LoadAsync(@"C:\Movies\Arrival.mkv", "Arrival", true, true);
        Expect(result is not null, "valid cached poster should load");
        Expect(handler.Calls == 0, "cache hit should avoid HTTP");
    }

    private static async Task ExactTitleYearAsync()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("/search/movie", StringComparison.Ordinal))
                return Json("""{"results":[{"id":335984,"title":"Blade Runner 2049","original_title":"Blade Runner 2049","release_date":"2017-10-04","poster_path":"/blade.jpg"}]}""");
            if (url.Contains("/search/tv", StringComparison.Ordinal))
                return Json("""{"results":[]}""");
            if (url.EndsWith("/3/configuration", StringComparison.Ordinal))
                return Json("""{"images":{"secure_base_url":"https://image.tmdb.org/t/p/","poster_sizes":["w342","w500","w780"]}}""");
            if (url.Contains("image.tmdb.org", StringComparison.Ordinal))
                return Bytes(TinyPng);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var http = new HttpClient(handler);
        var service = new TmdbArtworkService(http, "test-token", temp.Path);

        var result = await service.LoadAsync(
            @"C:\Movies\Blade Runner 2049 [2017].mkv",
            "Blade Runner 2049 [2017]",
            true,
            true);

        Expect(result is not null, "exact title/year poster should load");
        Expect(handler.Urls.Any(url => url.Contains("year=2017", StringComparison.Ordinal)), "movie search should carry year");
        Expect(handler.Urls.Any(url => url.Contains("/w500/blade.jpg", StringComparison.Ordinal)), "w500 poster should be requested");
    }

    private static async Task MovieWinsBeforeTvAsync()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;

            if (url.Contains("/search/movie", StringComparison.Ordinal))
                return Json("""{"results":[{"id":8337,"title":"They Live","original_title":"They Live","release_date":"1988-11-04","poster_path":"/they-live.jpg"}]}""");

            if (url.Contains("/search/tv", StringComparison.Ordinal))
                throw new InvalidOperationException("TV search should not run after an unambiguous movie match.");

            if (url.EndsWith("/3/configuration", StringComparison.Ordinal))
                return Json("""{"images":{"secure_base_url":"https://image.tmdb.org/t/p/","poster_sizes":["w500"]}}""");

            if (url.Contains("image.tmdb.org", StringComparison.Ordinal))
                return Bytes(TinyPng);

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler);
        var service = new TmdbArtworkService(http, "test-token", temp.Path);

        var result = await service.LoadAsync(
            @"Blu-ray disc • D:\",
            "They Live",
            true,
            false);

        Expect(result is not null, "unambiguous movie poster should load before TV fallback");
        Expect(!handler.Urls.Any(url => url.Contains("/search/tv", StringComparison.Ordinal)),
            "TV search should not run after movie match");
    }
    private static async Task LocalizedTitleCollisionPrefersOriginalAsync()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;

            if (url.Contains("/search/movie", StringComparison.Ordinal))
                return Json("""{"results":[{"id":8337,"title":"They Live","original_title":"They Live","release_date":"1988-11-04","poster_path":"/they-live.jpg"},{"id":515576,"title":"They Live","original_title":"Oni žive","release_date":"2018-04-01","poster_path":"/oni-zive.jpg"}]}""");

            if (url.Contains("/search/tv", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "TV search should not run after original-title disambiguation.");

            if (url.EndsWith("/3/configuration", StringComparison.Ordinal))
                return Json("""{"images":{"secure_base_url":"https://image.tmdb.org/t/p/","poster_sizes":["w500"]}}""");

            if (url.Contains("/w500/they-live.jpg", StringComparison.Ordinal))
                return Bytes(TinyPng);

            if (url.Contains("oni-zive.jpg", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Localized-title collision selected the wrong TMDB poster.");

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler);
        var service = new TmdbArtworkService(http, "test-token", temp.Path);

        var result = await service.LoadAsync(
            @"Blu-ray disc • D:\",
            "They Live",
            true,
            false);

        Expect(result is not null,
            "exact original_title should resolve localized-title collision");

        Expect(handler.Urls.Any(url =>
                url.Contains("/w500/they-live.jpg", StringComparison.Ordinal)),
            "They Live (1988) poster should be selected");

        Expect(!handler.Urls.Any(url =>
                url.Contains("oni-zive.jpg", StringComparison.Ordinal)),
            "Oni žive (2018) poster must not be selected");

        Expect(!handler.Urls.Any(url =>
                url.Contains("/search/tv", StringComparison.Ordinal)),
            "TV search should not run after original-title disambiguation");
    }

    private static async Task RuntimeDisambiguatesMovieAsync()
    {
        using var temp = new TempDirectory();

        var handler = new FakeHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;

            if (url.Contains("/search/movie", StringComparison.Ordinal))
                return Json("""{"results":[{"id":20766,"title":"The Road","original_title":"The Road","release_date":"2009-11-25","poster_path":"/qLaXnLzqleBWQtjvZ6JGVSaKoC3.jpg"},{"id":810163,"title":"The Road","original_title":"The Road","release_date":"2017-10-31","poster_path":"/dUXB2SyfkARWEbYEkjMz2xj4Rfe.jpg"},{"id":1522744,"title":"The Road","original_title":"The Road","release_date":"2025-08-15","poster_path":"/oZt6Ed1bs8HncfuvvHtQ1FArJ44.jpg"},{"id":1275644,"title":"The Road","original_title":"The Road","release_date":"1963-09-29","poster_path":null}]}""");

            if (url.Contains("/movie/20766", StringComparison.Ordinal))
                return Json("""{"id":20766,"runtime":111}""");

            if (url.Contains("/movie/810163", StringComparison.Ordinal))
                return Json("""{"id":810163,"runtime":2}""");

            if (url.Contains("/movie/1522744", StringComparison.Ordinal))
                return Json("""{"id":1522744,"runtime":12}""");

            if (url.EndsWith("/3/configuration", StringComparison.Ordinal))
                return Json("""{"images":{"secure_base_url":"https://image.tmdb.org/t/p/","poster_sizes":["w342","w500","w780"]}}""");

            if (url.Contains(
                    "/w500/qLaXnLzqleBWQtjvZ6JGVSaKoC3.jpg",
                    StringComparison.Ordinal))
                return Bytes(TinyPng);

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler);

        var service = new TmdbArtworkService(
            http,
            "test-token",
            temp.Path);

        var result = await service.LoadAsync(
            @"Blu-ray disc • D:\",
            "The Road - Blu-ray™",
            true,
            true,
            CancellationToken.None,
            TimeSpan.FromMinutes(111) + TimeSpan.FromSeconds(20));

        Expect(
            result is not null,
            "111-minute runtime should uniquely resolve The Road (2009)");

        Expect(
            handler.Urls.Any(url =>
                url.Contains("/movie/20766", StringComparison.Ordinal)),
            "runtime disambiguation should request movie details");

        Expect(
            handler.Urls.Any(url =>
                url.Contains(
                    "/w500/qLaXnLzqleBWQtjvZ6JGVSaKoC3.jpg",
                    StringComparison.Ordinal)),
            "runtime disambiguation should select the 2009 poster");

        var genericQuery = TmdbArtworkQuery.Create(
            @"Blu-ray disc • D:\",
            "The Road - Blu-ray™")!;

        Expect(
            !File.Exists(service.GetCachePath(genericQuery)),
            "runtime-resolved ambiguity must not poison the generic title cache");

        Expect(
            Directory.EnumerateFiles(temp.Path, "*.image").Count() == 1,
            "runtime-resolved poster should create exactly one runtime-specific cache entry");
    }

    private static async Task AmbiguousTitleRefusedAsync()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("/search/movie", StringComparison.Ordinal))
                return Json("""{"results":[{"id":1,"title":"The Thing","original_title":"The Thing","release_date":"1982-06-25","poster_path":"/a.jpg"},{"id":2,"title":"The Thing","original_title":"The Thing","release_date":"2011-10-14","poster_path":"/b.jpg"}]}""");
            if (url.Contains("/search/tv", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Ambiguous movie lookup must not fall through to TV.");

            throw new InvalidOperationException(
                "Ambiguous lookup must not request configuration or poster.");
        });
        using var http = new HttpClient(handler);
        var service = new TmdbArtworkService(http, "test-token", temp.Path);

        var result = await service.LoadAsync(@"C:\Movies\The Thing.mkv", "The Thing", true, false);
        Expect(result is null, "ambiguous title-only result must be refused");

        Expect(!handler.Urls.Any(url =>
                url.Contains("/search/tv", StringComparison.Ordinal)),
            "ambiguous movie result should stop before TV fallback");

        Expect(!handler.Urls.Any(url =>
                url.Contains("/configuration", StringComparison.Ordinal)),
            "ambiguous result should stop before image configuration");
    }

    private static async Task NetworkFailureAsync()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHandler(_ => throw new HttpRequestException("offline"));
        using var http = new HttpClient(handler);
        var service = new TmdbArtworkService(http, "test-token", temp.Path);

        var result = await service.LoadAsync(@"C:\Movies\Arrival.mkv", "Arrival", true, false);
        Expect(result is null, "network failure must degrade to null");
    }

    private static async Task CorruptPosterRejectedAsync()
    {
        using var temp = new TempDirectory();
        var handler = HappySearchHandler(Encoding.UTF8.GetBytes("not an image"));
        using var http = new HttpClient(handler);
        var service = new TmdbArtworkService(http, "test-token", temp.Path);

        var result = await service.LoadAsync(@"C:\Movies\Arrival [2016].mkv", "Arrival [2016]", true, true);
        Expect(result is null, "corrupt poster must be rejected");
    }

    private static async Task OversizedPosterRejectedAsync()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("/search/movie", StringComparison.Ordinal))
                return Json("""{"results":[{"id":329865,"title":"Arrival","release_date":"2016-11-10","poster_path":"/arrival.jpg"}]}""");
            if (url.Contains("/search/tv", StringComparison.Ordinal))
                return Json("""{"results":[]}""");
            if (url.EndsWith("/3/configuration", StringComparison.Ordinal))
                return Json("""{"images":{"secure_base_url":"https://image.tmdb.org/t/p/","poster_sizes":["w500"]}}""");
            if (url.Contains("image.tmdb.org", StringComparison.Ordinal))
            {
                var response = Bytes(TinyPng);
                response.Content.Headers.ContentLength = 20L * 1024 * 1024;
                return response;
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var http = new HttpClient(handler);
        var service = new TmdbArtworkService(http, "test-token", temp.Path);

        var result = await service.LoadAsync(@"C:\Movies\Arrival [2016].mkv", "Arrival [2016]", true, false);
        Expect(result is null, "oversized declared poster must be rejected");
    }

    private static async Task CancellationAsync()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHandler(async (request, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return Json("""{"results":[]}""");
        });
        using var http = new HttpClient(handler);
        var service = new TmdbArtworkService(http, "test-token", temp.Path);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        try
        {
            await service.LoadAsync(@"C:\Movies\Arrival.mkv", "Arrival", true, false, cancellation.Token);
            throw new InvalidOperationException("cancellation should propagate");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static Task OpticalMetadataBrandingAsync()
    {
        var query = TmdbArtworkQuery.Create(
            @"Blu-ray disc • D:\",
            "The Road - Blu-ray™")!;

        Expect(query.Title == "The Road",
            "Blu-ray branding should be removed from TMDB search title");

        Expect(query.NormalizedTitle == "the road",
            "branded Blu-ray metadata should normalize to the road");

        Expect(query.CacheIdentity == "bluray|the road|",
            "branded Blu-ray metadata should use the clean optical cache identity");

        return Task.CompletedTask;
    }

    private static Task OpticalIdentityAsync()
    {
        var first = TmdbArtworkQuery.Create(@"Blu-ray disc • D:\", "They Live")!;
        var second = TmdbArtworkQuery.Create(@"Blu-ray disc • E:\", "They Live")!;
        Expect(first.CacheKey == second.CacheKey, "optical cache key should not depend on drive letter");

        var titleSpecific = TmdbArtworkQuery.Create(@"Blu-ray disc • D:\ • title 1", "They Live — Title 1")!;
        Expect(first.CacheKey == titleSpecific.CacheKey, "structural optical title suffix should not split the movie poster cache");
        return Task.CompletedTask;
    }

    private static FakeHandler HappySearchHandler(byte[] posterBytes) => new(request =>
    {
        var url = request.RequestUri!.AbsoluteUri;
        if (url.Contains("/search/movie", StringComparison.Ordinal))
            return Json("""{"results":[{"id":329865,"title":"Arrival","release_date":"2016-11-10","poster_path":"/arrival.jpg"}]}""");
        if (url.Contains("/search/tv", StringComparison.Ordinal))
            return Json("""{"results":[]}""");
        if (url.EndsWith("/3/configuration", StringComparison.Ordinal))
            return Json("""{"images":{"secure_base_url":"https://image.tmdb.org/t/p/","poster_sizes":["w500"]}}""");
        if (url.Contains("image.tmdb.org", StringComparison.Ordinal))
            return Bytes(posterBytes);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    });

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes)
    };

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
        public int Calls { get; private set; }
        public List<string> Urls { get; } = [];

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            : this((request, _) => Task.FromResult(handler(request)))
        {
        }

        public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Urls.Add(request.RequestUri!.AbsoluteUri);
            return await _handler(request, cancellationToken);
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "ParallaxStudio-TmdbTests", Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
