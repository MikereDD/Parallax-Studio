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
            ("DVD TV box-set season year resolves series", DvdTvBoxSetSeasonYearAsync),
            ("DVD TV box-set ambiguous season year is refused", DvdTvBoxSetAmbiguityAsync),
            ("ambiguous title-only lookup is refused", AmbiguousTitleRefusedAsync),
            ("network failure degrades cleanly", NetworkFailureAsync),
            ("corrupt poster is rejected", CorruptPosterRejectedAsync),
            ("oversized poster is rejected", OversizedPosterRejectedAsync),
            ("cancellation is honored", CancellationAsync),
            ("optical metadata branding is stripped", OpticalMetadataBrandingAsync),
            ("optical cache identity ignores drive letter", OpticalIdentityAsync),
            ("direct match returns candidate overview", DirectMovieOverviewAsync),
            ("runtime-disambiguated match returns its overview", RuntimeDisambiguatedOverviewAsync),
            ("ambiguous candidates return no overview", AmbiguousNoOverviewAsync),
            ("DVD box-set match returns its overview", DvdTvBoxSetOverviewAsync),
            ("poster and metadata survive cache-only reload", CacheOnlyReloadAsync),
            ("corrupt metadata cache keeps poster", CorruptMetadataCacheAsync),
            ("missing overview does not invalidate poster", MissingOverviewValidAsync),
            ("runtime cache identity isolates overviews", RuntimeIdentityIsolationAsync),
            ("rejected candidate never exposes overview", RejectedCandidateNoOverviewAsync),
            ("metadata cache writes remain bounded", BoundedMetadataWriteAsync),
            ("invalid metadata fields preserve poster without plot", InvalidMetadataFieldsAsync),
            ("oversized metadata preserves poster without plot", OversizedMetadataAsync),
            ("cached overview is normalized and bounded", CachedOverviewBoundsAsync),
            ("failed metadata replacement never pairs old plot with new poster", FailedMetadataReplacementAsync),
            ("unverifiable poster association preserves poster without metadata", UnverifiablePosterAssociationAsync)
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
        Expect(result!.Overview is null && result.TmdbId is null, "legacy poster cache without metadata must remain usable without invented identity or plot");
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

    private static async Task DvdTvBoxSetSeasonYearAsync()
    {
        using var temp = new TempDirectory();

        var handler = new FakeHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;

            if (url.Contains("/search/movie", StringComparison.Ordinal))
                return Json("""{"results":[]}""");

            if (url.Contains("/search/tv", StringComparison.Ordinal))
            {
                if (url.Contains("query=DR%20WHO&", StringComparison.Ordinal))
                {
                    return Json(
                        """{"results":[{"id":57243,"name":"Doctor Who","original_name":"Doctor Who","first_air_date":"2005-03-26","poster_path":"/doctor-who.jpg"},{"id":121,"name":"Doctor Who","original_name":"Doctor Who","first_air_date":"1963-11-23","poster_path":"/classic-who.jpg"},{"id":1056,"name":"Doctor Who Confidential","original_name":"Doctor Who Confidential","first_air_date":"2005-03-26","poster_path":"/confidential.jpg"}]}""");
                }

                return Json("""{"results":[]}""");
            }

            if (url.Contains("/tv/57243?", StringComparison.Ordinal))
            {
                return Json(
                    """{"id":57243,"seasons":[{"season_number":11,"name":"Series 11","air_date":"2018-10-07","episode_count":10}]}""");
            }

            if (url.Contains("/tv/121?", StringComparison.Ordinal))
            {
                return Json(
                    """{"id":121,"seasons":[{"season_number":26,"name":"Season 26","air_date":"1989-09-06","episode_count":14}]}""");
            }

            if (url.EndsWith("/3/configuration", StringComparison.Ordinal))
            {
                return Json(
                    """{"images":{"secure_base_url":"https://image.tmdb.org/t/p/","poster_sizes":["w342","w500","w780"]}}""");
            }

            if (url.Contains(
                    "/w500/doctor-who.jpg",
                    StringComparison.Ordinal))
            {
                return Bytes(TinyPng);
            }

            return new HttpResponseMessage(
                HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler);

        var service = new TmdbArtworkService(
            http,
            "test-token",
            temp.Path);

        var result = await service.LoadAsync(
            @"DVD disc • D:\ • DVD title 1",
            "DR WHO COMPLETE SERIES 2018 D13 — 13 chapter(s), 2.1 GB (C1)",
            true,
            true);

        Expect(
            result is not null,
            "2018 DVD box-set label should resolve Doctor Who (2005)");

        Expect(
            handler.Urls.Any(url =>
                url.Contains(
                    "query=DR%20WHO&",
                    StringComparison.Ordinal)),
            "DVD box-set fallback should search the cleaned TV title");

        Expect(
            handler.Urls.Any(url =>
                url.Contains(
                    "/tv/57243?",
                    StringComparison.Ordinal)),
            "DVD box-set fallback should inspect the 2005 series seasons");

        Expect(
            handler.Urls.Any(url =>
                url.Contains(
                    "/tv/121?",
                    StringComparison.Ordinal)),
            "DVD box-set fallback should inspect the classic series seasons");

        Expect(
            handler.Urls.Any(url =>
                url.Contains(
                    "/w500/doctor-who.jpg",
                    StringComparison.Ordinal)),
            "DVD box-set season-year match should select the 2005 series poster");

        var query = TmdbArtworkQuery.Create(
            @"DVD disc • D:\ • DVD title 1",
            "DR WHO COMPLETE SERIES 2018 D13 — 13 chapter(s), 2.1 GB (C1)")!;

        Expect(
            File.Exists(service.GetCachePath(query)),
            "DVD box-set poster should use the normal disc-label cache");
    }

    private static async Task DvdTvBoxSetAmbiguityAsync()
    {
        using var temp = new TempDirectory();

        var handler = new FakeHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;

            if (url.Contains("/search/movie", StringComparison.Ordinal))
                return Json("""{"results":[]}""");

            if (url.Contains("/search/tv", StringComparison.Ordinal))
            {
                if (url.Contains("query=DR%20WHO&", StringComparison.Ordinal))
                {
                    return Json(
                        """{"results":[{"id":1,"name":"Doctor Who","original_name":"Doctor Who","first_air_date":"2005-03-26","poster_path":"/one.jpg"},{"id":2,"name":"Doctor Who","original_name":"Doctor Who","first_air_date":"1963-11-23","poster_path":"/two.jpg"}]}""");
                }

                return Json("""{"results":[]}""");
            }

            if (url.Contains("/tv/1?", StringComparison.Ordinal) ||
                url.Contains("/tv/2?", StringComparison.Ordinal))
            {
                return Json(
                    """{"seasons":[{"season_number":1,"air_date":"2018-01-01","episode_count":10}]}""");
            }

            if (url.EndsWith("/3/configuration", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Ambiguous DVD TV box-set lookup must not request image configuration.");
            }

            if (url.Contains(
                    "image.tmdb.org",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Ambiguous DVD TV box-set lookup must not request a poster.");
            }

            return new HttpResponseMessage(
                HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler);

        var service = new TmdbArtworkService(
            http,
            "test-token",
            temp.Path);

        var result = await service.LoadAsync(
            @"DVD disc • D:\ • DVD title 1",
            "DR WHO COMPLETE SERIES 2018 D13 — 13 chapter(s), 2.1 GB (C1)",
            true,
            false);

        Expect(
            result is null,
            "multiple exact series with the same season year must remain ambiguous");
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

    private static async Task DirectMovieOverviewAsync()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("/search/movie", StringComparison.Ordinal))
                return Json("""{"results":[{"id":329865,"title":"Arrival","original_title":"Arrival","release_date":"2016-11-10","poster_path":"/arrival.jpg","overview":"A linguist works with the military to communicate with alien lifeforms."}]}""");
            if (url.EndsWith("/3/configuration", StringComparison.Ordinal))
                return Json("""{"images":{"secure_base_url":"https://image.tmdb.org/t/p/","poster_sizes":["w500"]}}""");
            if (url.Contains("image.tmdb.org", StringComparison.Ordinal))
                return Bytes(TinyPng);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var http = new HttpClient(handler);
        var service = new TmdbArtworkService(http, "test-token", temp.Path);

        var result = await service.LoadAsync(@"C:\Movies\Arrival.mkv", "Arrival", true, true);

        Expect(result is not null, "direct match should return a result");
        Expect(result!.TmdbId == 329865, "result should identify the accepted TMDB movie");
        Expect(result.MediaType == "movie", "result should carry the media type");
        Expect(result.Overview == "A linguist works with the military to communicate with alien lifeforms.",
            "direct match should return the accepted candidate's overview");
        Expect(Directory.EnumerateFiles(temp.Path, "*.json").Count() == 1,
            "accepted match should write one metadata cache entry");
    }

    private static async Task RuntimeDisambiguatedOverviewAsync()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("/search/movie", StringComparison.Ordinal))
                return Json("""{"results":[{"id":20766,"title":"The Road","original_title":"The Road","release_date":"2009-11-25","poster_path":"/road2009.jpg","overview":"A father and his son walk alone through burned America."},{"id":810163,"title":"The Road","original_title":"The Road","release_date":"2017-10-31","poster_path":"/road2017.jpg","overview":"A wrong short film overview."}]}""");
            if (url.Contains("/movie/20766", StringComparison.Ordinal))
                return Json("""{"id":20766,"runtime":111}""");
            if (url.Contains("/movie/810163", StringComparison.Ordinal))
                return Json("""{"id":810163,"runtime":2}""");
            if (url.EndsWith("/3/configuration", StringComparison.Ordinal))
                return Json("""{"images":{"secure_base_url":"https://image.tmdb.org/t/p/","poster_sizes":["w500"]}}""");
            if (url.Contains("/w500/road2009.jpg", StringComparison.Ordinal))
                return Bytes(TinyPng);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var http = new HttpClient(handler);
        var service = new TmdbArtworkService(http, "test-token", temp.Path);

        var result = await service.LoadAsync(
            @"Blu-ray disc • D:\",
            "The Road - Blu-ray™",
            true,
            true,
            CancellationToken.None,
            TimeSpan.FromMinutes(111));

        Expect(result is not null, "runtime disambiguation should resolve");
        Expect(result!.TmdbId == 20766, "runtime resolution should select the 2009 film");
        Expect(result.Overview == "A father and his son walk alone through burned America.",
            "runtime-disambiguated result must carry the resolved candidate's overview");
        Expect(Directory.EnumerateFiles(temp.Path, "*.json").Count() == 1,
            "runtime-disambiguated metadata should use exactly one runtime-specific cache entry");
    }

    private static async Task AmbiguousNoOverviewAsync()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("/search/movie", StringComparison.Ordinal))
                return Json("""{"results":[{"id":1,"title":"The Road","original_title":"The Road","release_date":"2009-11-25","poster_path":"/a.jpg","overview":"First candidate plot."},{"id":2,"title":"The Road","original_title":"The Road","release_date":"2011-01-01","poster_path":"/b.jpg","overview":"Second candidate plot."}]}""");
            if (url.Contains("/search/tv", StringComparison.Ordinal))
                throw new InvalidOperationException("movie ambiguity must not fall back to TV");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var http = new HttpClient(handler);
        var service = new TmdbArtworkService(http, "test-token", temp.Path);

        var result = await service.LoadAsync(@"C:\Movies\The Road.mkv", "The Road", true, true);

        Expect(result is null, "ambiguous candidates must return no result and no overview");
        Expect(!Directory.EnumerateFiles(temp.Path).Any(), "refused lookup must not write cache entries");
    }

    private static async Task DvdTvBoxSetOverviewAsync()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("/search/movie", StringComparison.Ordinal))
                return Json("""{"results":[]}""");
            if (url.Contains("/search/tv", StringComparison.Ordinal))
            {
                if (url.Contains("query=DR%20WHO&", StringComparison.Ordinal))
                    return Json("""{"results":[{"id":57243,"name":"Doctor Who","original_name":"Doctor Who","first_air_date":"2005-03-26","poster_path":"/doctor-who.jpg","overview":"The adventures of a Time Lord."},{"id":121,"name":"Doctor Who","original_name":"Doctor Who","first_air_date":"1963-11-23","poster_path":"/classic-who.jpg","overview":"Classic series plot."}]}""");
                return Json("""{"results":[]}""");
            }
            if (url.Contains("/tv/57243?", StringComparison.Ordinal))
                return Json("""{"id":57243,"seasons":[{"season_number":11,"name":"Series 11","air_date":"2018-10-07","episode_count":10}]}""");
            if (url.Contains("/tv/121?", StringComparison.Ordinal))
                return Json("""{"id":121,"seasons":[{"season_number":26,"name":"Season 26","air_date":"1989-09-06","episode_count":14}]}""");
            if (url.EndsWith("/3/configuration", StringComparison.Ordinal))
                return Json("""{"images":{"secure_base_url":"https://image.tmdb.org/t/p/","poster_sizes":["w500"]}}""");
            if (url.Contains("/w500/doctor-who.jpg", StringComparison.Ordinal))
                return Bytes(TinyPng);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var http = new HttpClient(handler);
        var service = new TmdbArtworkService(http, "test-token", temp.Path);

        var result = await service.LoadAsync(
            @"DVD disc • D:\ • DVD title 1",
            "DR WHO COMPLETE SERIES 2018 D13 — 13 chapter(s), 2.1 GB (C1)",
            true,
            true);

        Expect(result is not null, "DVD box-set match should resolve");
        Expect(result!.MediaType == "tv", "box-set result should identify as TV");
        Expect(result.Overview == "The adventures of a Time Lord.",
            "box-set result must carry the accepted TV candidate's overview");
    }

    private static async Task CacheOnlyReloadAsync()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("/search/movie", StringComparison.Ordinal))
                return Json("""{"results":[{"id":329865,"title":"Arrival","original_title":"Arrival","release_date":"2016-11-10","poster_path":"/arrival.jpg","overview":"A linguist works with the military to communicate with alien lifeforms."}]}""");
            if (url.EndsWith("/3/configuration", StringComparison.Ordinal))
                return Json("""{"images":{"secure_base_url":"https://image.tmdb.org/t/p/","poster_sizes":["w500"]}}""");
            if (url.Contains("image.tmdb.org", StringComparison.Ordinal))
                return Bytes(TinyPng);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var http = new HttpClient(handler);
        var online = new TmdbArtworkService(http, "test-token", temp.Path);

        var first = await online.LoadAsync(@"C:\Movies\Arrival.mkv", "Arrival", true, true);
        Expect(first?.Overview is not null, "online resolution should capture the overview");

        var offlineHandler = new FakeHandler(_ => throw new InvalidOperationException("cache-only reload must not issue HTTP"));
        using var offlineHttp = new HttpClient(offlineHandler);
        var offline = new TmdbArtworkService(offlineHttp, null, temp.Path);

        var reloaded = await offline.LoadAsync(@"C:\Movies\Arrival.mkv", "Arrival", true, true);

        Expect(reloaded is not null, "cache-only reload should return a result");
        Expect(reloaded!.Overview == "A linguist works with the military to communicate with alien lifeforms.",
            "cache-only reload should restore the cached overview");
        Expect(reloaded.TmdbId == 329865, "cache-only reload should restore the accepted TMDB id");
        Expect(offlineHandler.Calls == 0, "cache-only reload should not issue HTTP");
    }

    private static async Task CorruptMetadataCacheAsync()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHandler(_ => throw new InvalidOperationException("cache hit should not issue HTTP"));
        using var http = new HttpClient(handler);
        var service = new TmdbArtworkService(http, null, temp.Path);
        var query = TmdbArtworkQuery.Create(@"C:\Movies\Arrival.mkv", "Arrival")!;
        var posterPath = service.GetCachePath(query);
        await File.WriteAllBytesAsync(posterPath, TinyPng);
        await File.WriteAllTextAsync(Path.ChangeExtension(posterPath, ".json"), "{ not valid json");

        var result = await service.LoadAsync(@"C:\Movies\Arrival.mkv", "Arrival", true, true);

        Expect(result is not null, "corrupt metadata must not invalidate the cached poster");
        Expect(result!.Overview is null, "corrupt metadata should yield no overview");
        Expect(handler.Calls == 0, "corrupt metadata should not trigger HTTP");
    }

    private static async Task MissingOverviewValidAsync()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("/search/movie", StringComparison.Ordinal))
                return Json("""{"results":[{"id":329865,"title":"Arrival","original_title":"Arrival","release_date":"2016-11-10","poster_path":"/arrival.jpg"}]}""");
            if (url.EndsWith("/3/configuration", StringComparison.Ordinal))
                return Json("""{"images":{"secure_base_url":"https://image.tmdb.org/t/p/","poster_sizes":["w500"]}}""");
            if (url.Contains("image.tmdb.org", StringComparison.Ordinal))
                return Bytes(TinyPng);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var http = new HttpClient(handler);
        var service = new TmdbArtworkService(http, "test-token", temp.Path);

        var result = await service.LoadAsync(@"C:\Movies\Arrival.mkv", "Arrival", true, true);

        Expect(result is not null, "accepted candidate without overview should still return a result");
        Expect(result!.Poster is not null, "poster must survive a missing overview");
        Expect(result.Overview is null, "missing overview should be null, not fabricated");
        var calls = handler.Calls;
        var cached = await service.LoadAsync(@"C:\Movies\Arrival.mkv", "Arrival", true, true);
        Expect(cached is not null && cached.TmdbId == 329865 && cached.Overview is null,
            "cached accepted identity without an overview must remain valid");
        Expect(handler.Calls == calls, "missing cached overview must not trigger a replacement lookup");
    }

    private static async Task RuntimeIdentityIsolationAsync()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("/search/movie", StringComparison.Ordinal))
                return Json("""{"results":[{"id":20766,"title":"The Road","original_title":"The Road","release_date":"2009-11-25","poster_path":"/road2009.jpg","overview":"The 2009 plot."},{"id":810163,"title":"The Road","original_title":"The Road","release_date":"2017-10-31","poster_path":"/road2017.jpg","overview":"The 2017 plot."}]}""");
            if (url.Contains("/movie/20766", StringComparison.Ordinal))
                return Json("""{"id":20766,"runtime":111}""");
            if (url.Contains("/movie/810163", StringComparison.Ordinal))
                return Json("""{"id":810163,"runtime":2}""");
            if (url.EndsWith("/3/configuration", StringComparison.Ordinal))
                return Json("""{"images":{"secure_base_url":"https://image.tmdb.org/t/p/","poster_sizes":["w500"]}}""");
            if (url.Contains("/w500/road2009.jpg", StringComparison.Ordinal))
                return Bytes(TinyPng);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var http = new HttpClient(handler);
        var online = new TmdbArtworkService(http, "test-token", temp.Path);

        var resolved = await online.LoadAsync(
            @"Blu-ray disc • D:\", "The Road - Blu-ray™", true, true,
            CancellationToken.None, TimeSpan.FromMinutes(111));
        Expect(resolved?.Overview == "The 2009 plot.", "online runtime resolution should capture the 2009 overview");

        var offlineHandler = new FakeHandler(_ => throw new InvalidOperationException("cache-only load must not issue HTTP"));
        using var offlineHttp = new HttpClient(offlineHandler);
        var offline = new TmdbArtworkService(offlineHttp, null, temp.Path);

        var sameRuntime = await offline.LoadAsync(
            @"Blu-ray disc • E:\", "The Road - Blu-ray™", true, true,
            CancellationToken.None, TimeSpan.FromMinutes(111));
        Expect(sameRuntime?.Overview == "The 2009 plot.",
            "matching runtime identity should restore the 2009 overview from cache");

        var differentRuntime = await offline.LoadAsync(
            @"Blu-ray disc • D:\", "The Road - Blu-ray™", true, true,
            CancellationToken.None, TimeSpan.FromMinutes(60));
        Expect(differentRuntime is null,
            "a different runtime identity must not inherit the 2009 overview");
        Expect(offlineHandler.Calls == 0, "cache-only runtime loads should not issue HTTP");
    }

    private static async Task RejectedCandidateNoOverviewAsync()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("/search/movie", StringComparison.Ordinal))
                return Json("""{"results":[{"id":1,"title":"Arrival","original_title":"Arrival","release_date":"1996-01-01","poster_path":"/wrong-year.jpg","overview":"Wrong-year candidate plot."}]}""");
            if (url.Contains("/search/tv", StringComparison.Ordinal))
                return Json("""{"results":[]}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var http = new HttpClient(handler);
        var service = new TmdbArtworkService(http, "test-token", temp.Path);

        var result = await service.LoadAsync(@"C:\Movies\Arrival [2016].mkv", "Arrival [2016]", true, true);

        Expect(result is null, "year-mismatched candidate must be refused with no result");
        Expect(!handler.Urls.Any(url => url.Contains("wrong-year.jpg", StringComparison.Ordinal)),
            "rejected candidate's poster must never be requested");
        Expect(!Directory.EnumerateFiles(temp.Path).Any(), "rejected lookup must not write cache entries");
    }

    private static async Task BoundedMetadataWriteAsync()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/search/movie", StringComparison.Ordinal))
                return Json(System.Text.Json.JsonSerializer.Serialize(new { results = new[] { new {
                    id = 329865, title = new string('X', 70000), original_title = "Arrival",
                    release_date = "2016-11-10", poster_path = "/arrival.jpg", overview = new string('Y', 5000)
                } } }));
            if (request.RequestUri!.AbsolutePath.EndsWith("/configuration", StringComparison.Ordinal))
                return Json("""{"images":{"secure_base_url":"https://image.tmdb.org/t/p/","poster_sizes":["w500"]}}""");
            return Bytes(TinyPng);
        });
        using var http = new HttpClient(handler);
        var service = new TmdbArtworkService(http, "test-token", temp.Path);
        var result = await service.LoadAsync(@"C:\Movies\Arrival.mkv", "Arrival", true, true);
        Expect(result?.Overview?.Length == 4000, "resolved overview must remain bounded");
        Expect(Directory.EnumerateFiles(temp.Path, "*.image").Count() == 1, "oversized metadata must not prevent poster caching");
        Expect(Directory.EnumerateFiles(temp.Path, "*.json").All(path => new FileInfo(path).Length <= 65536),
            "metadata writes must enforce the 64 KiB limit even with an oversized matched title");
        Expect(!Directory.EnumerateFiles(temp.Path, "*.tmp").Any(), "atomic cache writes must leave no temporary files");
    }

    private static async Task InvalidMetadataFieldsAsync()
    {
        using var temp = new TempDirectory();
        using var http = new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("cache hit must not issue HTTP")));
        var service = new TmdbArtworkService(http, null, temp.Path);
        var query = TmdbArtworkQuery.Create(@"C:\Movies\Arrival.mkv", "Arrival")!;
        var poster = service.GetCachePath(query);
        await File.WriteAllBytesAsync(poster, TinyPng);
        foreach (var json in new[] {
            """{"TmdbId":329865,"MediaType":"person","Title":"Arrival","Year":2016,"Overview":"Invalid metadata plot"}""",
            """{"TmdbId":329865,"Title":"Arrival","Overview":"Incomplete metadata plot"}""",
            """{"TmdbId":0,"MediaType":"movie","Title":"Arrival","Overview":"Invalid ID plot"}"""
        })
        {
            var fields = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;
            fields["PosterSha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(TinyPng));
            await File.WriteAllTextAsync(Path.ChangeExtension(poster, ".json"), System.Text.Json.JsonSerializer.Serialize(fields));
            var result = await service.LoadAsync(@"C:\Movies\Arrival.mkv", "Arrival", true, true);
            Expect(result is not null && result.Overview is null, "invalid metadata must not expose plot or invalidate poster");
        }
    }

    private static async Task OversizedMetadataAsync()
    {
        using var temp = new TempDirectory();
        using var http = new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("cache hit must not issue HTTP")));
        var service = new TmdbArtworkService(http, null, temp.Path);
        var query = TmdbArtworkQuery.Create(@"C:\Movies\Arrival.mkv", "Arrival")!;
        var poster = service.GetCachePath(query);
        await File.WriteAllBytesAsync(poster, TinyPng);
        await File.WriteAllTextAsync(Path.ChangeExtension(poster, ".json"), new string('X', 65537));
        var result = await service.LoadAsync(@"C:\Movies\Arrival.mkv", "Arrival", true, true);
        Expect(result is not null && result.Overview is null, "oversized metadata must not invalidate poster or expose plot");
    }

    private static async Task CachedOverviewBoundsAsync()
    {
        using var temp = new TempDirectory();
        using var http = new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("cache hit must not issue HTTP")));
        var service = new TmdbArtworkService(http, null, temp.Path);
        var poster = service.GetCachePath(TmdbArtworkQuery.Create(@"C:\Movies\Arrival.mkv", "Arrival")!);
        await File.WriteAllBytesAsync(poster, TinyPng);
        foreach (var overview in new[] { "  " + new string('X', 5000) + "  ", "   " })
        {
            await File.WriteAllTextAsync(Path.ChangeExtension(poster, ".json"),
                System.Text.Json.JsonSerializer.Serialize(new { TmdbId = 329865, MediaType = "movie", Title = "Arrival", Overview = overview,
                    PosterSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(TinyPng)) }));
            var result = await service.LoadAsync(@"C:\Movies\Arrival.mkv", "Arrival", true, true);
            Expect(result is not null && (string.IsNullOrWhiteSpace(overview)
                ? result.Overview is null
                : result.Overview == new string('X', 4000)), "cached overview must use the same normalization and length limit as online metadata");
        }
    }

    private static async Task FailedMetadataReplacementAsync()
    {
        using var temp = new TempDirectory();
        var replacement = false;
        var newPoster = TinyPng.Concat(new byte[] { 0 }).ToArray();
        var handler = new FakeHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("/search/movie", StringComparison.Ordinal))
                return Json(System.Text.Json.JsonSerializer.Serialize(new { results = new[] { new {
                    id = replacement ? 2 : 1, title = "Arrival", original_title = "Arrival",
                    release_date = "2016-11-10", poster_path = replacement ? "/new.jpg" : "/old.jpg",
                    overview = replacement ? "New accepted plot." : "Old accepted plot."
                } } }));
            if (url.EndsWith("/3/configuration", StringComparison.Ordinal))
                return Json("""{"images":{"secure_base_url":"https://image.tmdb.org/t/p/","poster_sizes":["w500"]}}""");
            if (url.Contains("image.tmdb.org", StringComparison.Ordinal))
                return Bytes(replacement ? newPoster : TinyPng);
            throw new InvalidOperationException("unexpected lookup");
        });
        using var http = new HttpClient(handler);
        var service = new TmdbArtworkService(http, "test-token", temp.Path);
        var first = await service.LoadAsync(@"C:\Movies\Arrival.mkv", "Arrival", true, true);
        Expect(first?.TmdbId == 1 && first.Overview == "Old accepted plot.", "initial accepted pair must be cached");
        var posterPath = service.GetCachePath(TmdbArtworkQuery.Create(@"C:\Movies\Arrival.mkv", "Arrival")!);
        var metadataPath = Path.ChangeExtension(posterPath, ".json");
        var oldMetadata = await File.ReadAllTextAsync(metadataPath);
        File.Delete(posterPath);
        replacement = true;
        // Windows permits reads but denies replacement/deletion of this sidecar.
        using (var lockedMetadata = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var resolved = await service.LoadAsync(@"C:\Movies\Arrival.mkv", "Arrival", true, true);
            Expect(resolved?.TmdbId == 2 && resolved.Overview == "New accepted plot.",
                "fresh result must use the replacement accepted candidate, even when metadata caching fails");
            Expect((await File.ReadAllBytesAsync(posterPath)).SequenceEqual(newPoster), "new valid poster bytes must have replaced the old poster");
            Expect(await File.ReadAllTextAsync(metadataPath) == oldMetadata, "sidecar lock must have prevented metadata replacement");
            Expect(!Directory.EnumerateFiles(temp.Path, "*.tmp").Any(), "failed atomic replacement must clean temporary files");
            var offlineHandler = new FakeHandler(_ => throw new InvalidOperationException("cache hit must not issue HTTP"));
            using var offlineHttp = new HttpClient(offlineHandler);
            var offline = new TmdbArtworkService(offlineHttp, null, temp.Path);
            var cached = await offline.LoadAsync(@"C:\Movies\Arrival.mkv", "Arrival", true, true);
            Expect(cached is not null && cached.Overview is null && cached.TmdbId is null,
                "new cached poster must remain usable without the stale candidate identity or plot");
            Expect(offlineHandler.Calls == 0, "unpaired metadata must not trigger HTTP");
        }
    }

    private static async Task UnverifiablePosterAssociationAsync()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHandler(_ => throw new InvalidOperationException("unverifiable metadata must not trigger HTTP"));
        using var http = new HttpClient(handler);
        var service = new TmdbArtworkService(http, "test-token", temp.Path);
        var posterPath = service.GetCachePath(TmdbArtworkQuery.Create(@"C:\Movies\Arrival.mkv", "Arrival")!);
        await File.WriteAllBytesAsync(posterPath, TinyPng);
        var matchingHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(TinyPng));
        var mismatchedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(TinyPng.Concat(new byte[] { 0 }).ToArray()));
        foreach (var hash in new string?[] { null, "", "not-a-sha256", mismatchedHash, matchingHash.ToLowerInvariant() })
        {
            var fields = new Dictionary<string, object> {
                ["TmdbId"] = 329865, ["MediaType"] = "movie", ["Title"] = "Arrival", ["Overview"] = "Verified plot."
            };
            if (hash is not null) fields["PosterSha256"] = hash;
            await File.WriteAllTextAsync(Path.ChangeExtension(posterPath, ".json"), System.Text.Json.JsonSerializer.Serialize(fields));
            var cached = await service.LoadAsync(@"C:\Movies\Arrival.mkv", "Arrival", true, true);
            Expect(cached is not null, "unverifiable association must preserve the cached poster");
            if (hash == matchingHash.ToLowerInvariant())
                Expect(cached!.TmdbId == 329865 && cached.Overview == "Verified plot.", "matching byte digest must restore metadata");
            else
                Expect(cached!.TmdbId is null && cached.Overview is null, "missing, malformed, or mismatched digest must not expose identity or plot");
        }
        Expect(handler.Calls == 0, "association validation must not request a replacement lookup");
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
