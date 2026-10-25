using ParallaxStudio.Core.Enums;
using ParallaxStudio.Core.Models;
using ParallaxStudio.Infrastructure.Playback;

namespace ParallaxStudio.Infrastructure.Tests;

/// <summary>
/// Audit finding F5: DVD title reconciliation. Every check calls the production code:
/// DvdTitleMatcher (full map and loaded-title match), MpvPlaybackService.ParseDvdEditionMap
/// (probe output parsing), and MpvPlaybackService's apply/reconcile path via the stub host.
/// </summary>
internal static partial class Program
{
    private static (string Name, Func<Task> Run)[] TitleMatchingChecks =>
    [
        ("map: distinct runtimes map uniquely and keep nonsequential selectors", Sync(DistinctRuntimesKeepNonsequentialSelectors)),
        ("map: probe output parsing feeds the same map (zero-based selector kept)", Sync(ProbeOutputPipeline)),
        ("map: systematic offset with near-equal runtimes is ambiguous, not swapped", Sync(SystematicOffsetIsAmbiguous)),
        ("map: identical runtimes are ambiguous for every competing title", Sync(IdenticalRuntimesAreAmbiguous)),
        ("map: two metadata titles competing for one DVD title are both refused", Sync(CompetingMetadataForOneSelector)),
        ("map: duplicate DVD titles for one metadata title are ambiguous", Sync(DuplicateSelectorsAreAmbiguous)),
        ("map: distant single candidate is unmatched, with the nearest title reported", Sync(DistantCandidateIsUnmatched)),
        ("map: runtime window boundary is explicit", Sync(RuntimeWindowBoundary)),
        ("map: ambiguity is local; unrelated titles still map", Sync(AmbiguityIsLocal)),
        ("map: aggregate and zero-length titles are ignored", Sync(AggregateAndEmptyTitlesIgnored)),
        ("map: outcome is identical for every input order", Sync(MapIsOrderIndependent)),
        ("loaded: unique runtime matches despite identical chapter counts elsewhere", Sync(LoadedUniqueRuntime)),
        ("loaded: near-equal runtimes with identical chapter counts are ambiguous", Sync(LoadedNearEqualSameChapters)),
        ("loaded: a one-chapter difference does not break a runtime tie", Sync(LoadedOneChapterDifferenceStaysAmbiguous)),
        ("loaded: a larger chapter difference excludes a candidate", Sync(LoadedChapterDifferenceExcludes)),
        ("loaded: missing runtime or no candidate is unmatched", Sync(LoadedUnmatched)),
        ("loaded: outcome is identical for every input order", Sync(LoadedIsOrderIndependent)),
        ("service: verified reconciliation preserves metadata 6 -> DVD title 8", ServiceReconciliationPreservesNonsequentialMappingAsync),
        ("service: ambiguous title is refused with an explicit reason", ServiceAmbiguousTitleRefusedAsync),
        ("service: ambiguous post-load evidence keeps the full-map mapping", ServiceAmbiguousLoadKeepsMappingAsync),
        ("service: conflicting post-load evidence withdraws the mapping", ServiceConflictWithdrawsMappingAsync)
    ];

    private static Func<Task> Sync(Action action) => () =>
    {
        action();
        return Task.CompletedTask;
    };

    // ----- full map -------------------------------------------------------------------

    // Shaped like the regression disc: eight MakeMKV titles, libdvdnav selectors in a
    // different order, libdvdnav runtimes 6–7 s longer. Metadata title 6 is DVD title 8.
    private static readonly BluRayTitle[] RegressionMetadata =
    [
        Meta(0, "1:07:26", 9), Meta(1, "0:49:10", 7), Meta(2, "0:50:02", 7), Meta(3, "0:46:51", 6),
        Meta(4, "0:44:36", 6), Meta(5, "0:58:47", 8), Meta(6, "0:12:05", 3), Meta(7, "0:03:20", 1)
    ];

    private static readonly DvdEditionInfo[] RegressionEditions =
    [
        Edition(0, "1:07:32"), Edition(1, "0:49:16"), Edition(2, "0:46:58"), Edition(3, "0:50:09"),
        Edition(4, "0:44:43"), Edition(5, "0:03:26"), Edition(6, "0:12:11"), Edition(7, "0:58:54")
    ];

    private static readonly Dictionary<int, int> RegressionTruth = new()
    {
        [0] = 0, [1] = 1, [2] = 3, [3] = 2, [4] = 4, [5] = 7, [6] = 6, [7] = 5
    };

    private static void DistinctRuntimesKeepNonsequentialSelectors()
    {
        var result = DvdTitleMatcher.BuildFullMap(RegressionMetadata, RegressionEditions);
        Check(result.Outcomes.All(outcome => outcome.Kind == DvdTitleMatchKind.Unique),
            "Every distinct title must map: " + Describe(result));
        Check(SameMap(result.Mapping, RegressionTruth), "Mapping must follow runtimes, not order: " + Describe(result));
        Check(result.Mapping[5] == 7, "Metadata title 6 must map to DVD title 8");
    }

    private static void ProbeOutputPipeline()
    {
        var stdout = string.Join('\n', RegressionEditions.Select(edition =>
            $"DVD_EDITION|{edition.Selector}|{edition.Duration.TotalSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}|0|{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"title {edition.Selector + 1}"))}"))
            + "\nnoise line\nDVD_EDITION|9|0|0|\n";
        var parsed = MpvPlaybackService.ParseDvdEditionMap(stdout);
        Check(parsed.Count == RegressionEditions.Length, $"Expected {RegressionEditions.Length} editions, parsed {parsed.Count}");
        var result = DvdTitleMatcher.BuildFullMap(RegressionMetadata, parsed);
        Check(SameMap(result.Mapping, RegressionTruth), "Parsed probe output must produce the same map: " + Describe(result));
        Check(result.Mapping[0] == 0, "The zero-based dvd://0 selector must be kept");
    }

    private static void SystematicOffsetIsAmbiguous()
    {
        // Truth: metadata 1 -> DVD 4 and metadata 2 -> DVD 6 (both +7 s). The 0:44:37 title is
        // closer to metadata 2 (3 s) than to metadata 1 (7 s). The baseline greedy matcher
        // mapped metadata 1 -> DVD 6 and metadata 2 -> DVD 4, both wrong.
        var result = DvdTitleMatcher.BuildFullMap(
            [Meta(0, "0:44:30"), Meta(1, "0:44:40")],
            [Edition(3, "0:44:37"), Edition(5, "0:44:47")]);
        Check(result.Mapping.Count == 0, "No mapping may be invented: " + Describe(result));
        Check(result.Outcomes.All(outcome => outcome.Kind == DvdTitleMatchKind.Ambiguous), "Both titles must be reported ambiguous");
        // Metadata 1 has one DVD title in range, but that title is also in range of metadata 2;
        // metadata 2 has two DVD titles in range. Both reasons must say so.
        Check(result.OutcomeFor(0)!.Reason.Contains("DVD title 4") && result.OutcomeFor(0)!.Reason.Contains("metadata title 2"),
            "Metadata 1's reason must name the competing metadata title: " + result.OutcomeFor(0)!.Reason);
        Check(result.OutcomeFor(1)!.Reason.Contains("DVD title 4") && result.OutcomeFor(1)!.Reason.Contains("DVD title 6"),
            "Metadata 2's reason must name both DVD titles: " + result.OutcomeFor(1)!.Reason);
    }

    private static void IdenticalRuntimesAreAmbiguous()
    {
        var result = DvdTitleMatcher.BuildFullMap(
            [Meta(0, "0:44:30", 7), Meta(1, "0:44:30", 7)],
            [Edition(5, "0:44:36"), Edition(3, "0:44:37")]);
        Check(result.Mapping.Count == 0, "Index order must not break a tie: " + Describe(result));
        Check(result.Outcomes.All(outcome => outcome.Kind == DvdTitleMatchKind.Ambiguous && outcome.Candidates.Count == 2),
            "Both titles must list both candidates");
    }

    private static void CompetingMetadataForOneSelector()
    {
        // Each metadata title has exactly one candidate, but it is the same DVD title.
        // The baseline matcher gave it to metadata 1 because of index order.
        var result = DvdTitleMatcher.BuildFullMap(
            [Meta(0, "0:44:30"), Meta(1, "0:44:44")],
            [Edition(7, "0:44:37")]);
        Check(result.Mapping.Count == 0, "Neither competing title may claim the DVD title: " + Describe(result));
        Check(result.OutcomeFor(0)!.Reason.Contains("metadata title 2") && result.OutcomeFor(1)!.Reason.Contains("metadata title 1"),
            "Each reason must name the competing metadata title");
    }

    private static void DuplicateSelectorsAreAmbiguous()
    {
        var result = DvdTitleMatcher.BuildFullMap(
            [Meta(0, "0:58:47")],
            [Edition(2, "0:58:54"), Edition(9, "0:58:54")]);
        Check(result.OutcomeFor(0)!.Kind == DvdTitleMatchKind.Ambiguous && result.Mapping.Count == 0,
            "Two DVD titles with the same runtime cannot be told apart: " + Describe(result));
    }

    private static void DistantCandidateIsUnmatched()
    {
        // The baseline tolerance (2.5 %) accepted a 60 s difference as a confident match.
        var result = DvdTitleMatcher.BuildFullMap([Meta(0, "0:44:00")], [Edition(2, "0:45:00")]);
        var outcome = result.OutcomeFor(0)!;
        Check(outcome.Kind == DvdTitleMatchKind.Unmatched && result.Mapping.Count == 0, "A 60 s difference is not the same program");
        Check(outcome.Reason.Contains("DVD title 3") && outcome.Reason.Contains("60 s"), "The nearest title must be reported: " + outcome.Reason);
    }

    private static void RuntimeWindowBoundary()
    {
        Check(DvdTitleMatcher.RuntimeWindow == TimeSpan.FromSeconds(15), "The documented window is 15 s");
        var inside = DvdTitleMatcher.BuildFullMap([Meta(0, "0:30:00")], [Edition(1, "0:30:15")]);
        var outside = DvdTitleMatcher.BuildFullMap([Meta(0, "0:30:00")], [Edition(1, "0:30:16")]);
        Check(inside.Mapping.TryGetValue(0, out var selector) && selector == 1, "15 s is inside the window");
        Check(outside.OutcomeFor(0)!.Kind == DvdTitleMatchKind.Unmatched, "16 s is outside the window");
    }

    private static void AmbiguityIsLocal()
    {
        var result = DvdTitleMatcher.BuildFullMap(
            [Meta(0, "0:44:30"), Meta(1, "0:44:40"), Meta(2, "1:07:26"), Meta(3, "0:12:05")],
            [Edition(3, "0:44:37"), Edition(5, "0:44:47"), Edition(0, "1:07:32"), Edition(6, "0:12:11")]);
        Check(SameMap(result.Mapping, new Dictionary<int, int> { [2] = 0, [3] = 6 }), "Unrelated titles must still map: " + Describe(result));
        Check(result.OutcomeFor(0)!.Kind == DvdTitleMatchKind.Ambiguous && result.OutcomeFor(1)!.Kind == DvdTitleMatchKind.Ambiguous,
            "The near-equal pair must stay ambiguous");
    }

    private static void AggregateAndEmptyTitlesIgnored()
    {
        var result = DvdTitleMatcher.BuildFullMap(
            [Meta(0, "0:44:36"), Meta(1, "13:00:00"), Meta(2, "0:00:00")],
            [Edition(4, "0:44:43"), Edition(0, "13:00:05"), Edition(8, "0:00:00")]);
        Check(SameMap(result.Mapping, new Dictionary<int, int> { [0] = 4 }), "Only the playable title maps: " + Describe(result));
        Check(result.Outcomes.Count == 1, "Aggregate and empty metadata titles are not considered");
    }

    private static void MapIsOrderIndependent()
    {
        var metadata = RegressionMetadata.Concat([Meta(8, "0:44:30"), Meta(9, "0:44:40")]).ToArray();
        var editions = RegressionEditions.Concat([Edition(10, "0:44:37"), Edition(11, "0:44:47")]).ToArray();
        var expected = Canonical(DvdTitleMatcher.BuildFullMap(metadata, editions));
        var random = new Random(5150);
        for (var round = 0; round < 50; round++)
        {
            var shuffledMetadata = metadata.OrderBy(_ => random.Next()).ToArray();
            var shuffledEditions = editions.OrderBy(_ => random.Next()).ToArray();
            var actual = Canonical(DvdTitleMatcher.BuildFullMap(shuffledMetadata, shuffledEditions));
            Check(actual == expected, $"Round {round}: outcome depends on input order\n  expected {expected}\n  actual   {actual}");
        }
    }

    // ----- loaded-title match ---------------------------------------------------------

    private static void LoadedUniqueRuntime()
    {
        var match = DvdTitleMatcher.MatchLoadedTitle(RegressionMetadata, Duration("0:44:43"), 6);
        Check(match.Kind == DvdTitleMatchKind.Unique && match.Title?.Index == 4, $"Expected metadata title 5, got {match.Kind}: {match.Reason}");
    }

    private static void LoadedNearEqualSameChapters()
    {
        // Baseline: 44:47 is 7 s from metadata 2 and 17 s from metadata 1, so it "matched"
        // metadata 2 even though a +7 s or a -17 s discrepancy are both plausible here.
        var match = DvdTitleMatcher.MatchLoadedTitle([Meta(0, "0:44:35", 7), Meta(1, "0:44:40", 7)], Duration("0:44:47"), 7);
        Check(match.Kind == DvdTitleMatchKind.Ambiguous && match.Title is null && match.Candidates.Count == 2, $"Expected ambiguity, got {match.Kind}: {match.Reason}");
    }

    private static void LoadedOneChapterDifferenceStaysAmbiguous()
    {
        var match = DvdTitleMatcher.MatchLoadedTitle([Meta(0, "0:44:30", 7), Meta(1, "0:44:33", 8)], Duration("0:44:37"), 8);
        Check(match.Kind == DvdTitleMatchKind.Ambiguous, $"A ±1 chapter difference is consistent, so this is still a tie: {match.Kind}: {match.Reason}");
    }

    private static void LoadedChapterDifferenceExcludes()
    {
        var match = DvdTitleMatcher.MatchLoadedTitle([Meta(0, "0:44:30", 4), Meta(1, "0:44:33", 8)], Duration("0:44:37"), 8);
        Check(match.Kind == DvdTitleMatchKind.Unique && match.Title?.Index == 1, $"A 4-chapter difference excludes metadata title 1: {match.Kind}: {match.Reason}");
    }

    private static void LoadedUnmatched()
    {
        Check(DvdTitleMatcher.MatchLoadedTitle(RegressionMetadata, TimeSpan.Zero, 6).Kind == DvdTitleMatchKind.Unmatched, "No runtime, no match");
        Check(DvdTitleMatcher.MatchLoadedTitle(RegressionMetadata, Duration("0:33:00"), 6).Kind == DvdTitleMatchKind.Unmatched, "No candidate, no match");
    }

    private static void LoadedIsOrderIndependent()
    {
        var metadata = new[] { Meta(0, "0:44:35", 7), Meta(1, "0:44:40", 7), Meta(2, "0:44:36", 2), Meta(3, "0:58:47", 8) };
        var random = new Random(1987);
        var expected = DescribeLoaded(DvdTitleMatcher.MatchLoadedTitle(metadata, Duration("0:44:47"), 7));
        for (var round = 0; round < 50; round++)
        {
            var actual = DescribeLoaded(DvdTitleMatcher.MatchLoadedTitle(metadata.OrderBy(_ => random.Next()).ToArray(), Duration("0:44:47"), 7));
            Check(actual == expected, $"Round {round}: {actual} != {expected}");
        }
    }

    // ----- MpvPlaybackService apply/reconcile path ------------------------------------

    private static async Task ServiceReconciliationPreservesNonsequentialMappingAsync()
    {
        var log = new TestLogger();
        await using var service = NewService(log);
        await service.SeedDvdTitleMapForTestingAsync(Disc, RegressionMetadata, RegressionEditions);
        Check(SameMap(service.GetDvdTitleMapForTesting(Disc), RegressionTruth), "The production apply path must store the unique map");

        UseLoadedTitle("0:58:54", 8);
        await service.OpenDvdTitleAsync(Disc, 5);
        var pid = log.StubPids().Last();
        Check(log.Contains($"STUB_TITLE {pid} 7"), "Metadata title 6 must start DVD selector 7 (DVD title 8)");
        await WaitUntilAsync(() => log.Contains("DVD title reconciled"), TimeSpan.FromSeconds(4), "post-load reconciliation");
        Check(log.Contains("matched metadata title 6"), "Reconciliation must confirm metadata title 6");
        Check(SameMap(service.GetDvdTitleMapForTesting(Disc), RegressionTruth), "A verified load must not change the map");

        var titles = await service.GetDvdTitlesAsync(Disc);
        var active = titles.Single(title => title.IsActive);
        Check(active.Index == 5 && active.IsDvdMappingVerified && active.DvdPlaybackTitleNumber == 7, "Title browser must show metadata 6 as verified DVD title 8");
        log.AssertNoErrors();
    }

    private static async Task ServiceAmbiguousTitleRefusedAsync()
    {
        var log = new TestLogger();
        await using var service = NewService(log);
        await service.SeedDvdTitleMapForTestingAsync(
            Disc,
            [Meta(0, "0:44:30"), Meta(1, "0:44:40"), Meta(2, "1:07:26")],
            [Edition(3, "0:44:37"), Edition(5, "0:44:47"), Edition(0, "1:07:32")]);
        Check(log.Contains("DVD metadata title 1 not mapped (ambiguous)"), "Ambiguity must be logged when the map is built");

        UseStub("normal");
        var refusal = await CaptureAsync<InvalidOperationException>(() => service.OpenDvdTitleAsync(Disc, 0));
        Check(refusal.Message.Contains("ambiguous") && refusal.Message.Contains("DVD title 4") && refusal.Message.Contains("metadata title 2"),
            "The refusal must say why: " + refusal.Message);
        var second = await CaptureAsync<InvalidOperationException>(() => service.OpenDvdTitleAsync(Disc, 1));
        Check(second.Message.Contains("ambiguous") && second.Message.Contains("DVD title 4") && second.Message.Contains("DVD title 6"),
            "The competing title's refusal must name both DVD titles: " + second.Message);
        Check(log.StubPids().Count == 0, "No host may be started for an ambiguous title");

        await service.OpenDvdTitleAsync(Disc, 2); // the unique title still plays
        Check(log.Contains($"STUB_TITLE {log.StubPids().Last()} 0"), "The unique title must still start DVD selector 0");
    }

    private static async Task ServiceAmbiguousLoadKeepsMappingAsync()
    {
        var log = new TestLogger();
        await using var service = NewService(log);
        // Full map: unique by runtime. Post-load: two metadata titles fit the loaded runtime
        // once chapters (unknown in the probe) are considered, so the load cannot verify.
        var metadata = new[] { Meta(0, "0:44:36", 6), Meta(1, "0:44:50", 6) };
        await service.SeedDvdTitleMapForTestingAsync(Disc, metadata, [Edition(4, "0:44:30"), Edition(6, "0:45:20")]);
        Check(SameMap(service.GetDvdTitleMapForTesting(Disc), new Dictionary<int, int> { [0] = 4 }), "Precondition: unique full-map entry");

        UseLoadedTitle("0:44:43", 6);
        await service.OpenDvdTitleAsync(Disc, 0);
        await WaitUntilAsync(() => log.Contains("could not verify DVD selector 4"), TimeSpan.FromSeconds(4), "the ambiguity warning");
        Check(SameMap(service.GetDvdTitleMapForTesting(Disc), new Dictionary<int, int> { [0] = 4 }), "Ambiguous evidence must not change the map");
        Check(!log.Contains("DVD title reconciled"), "Ambiguous evidence must not be reported as a reconciliation");
    }

    private static async Task ServiceConflictWithdrawsMappingAsync()
    {
        var log = new TestLogger();
        await using var service = NewService(log);
        await service.SeedDvdTitleMapForTestingAsync(Disc, RegressionMetadata, RegressionEditions);

        // Metadata 5 maps to DVD title 5, but the loaded program has metadata 6's runtime.
        UseLoadedTitle("0:58:54", 8);
        await service.OpenDvdTitleAsync(Disc, 4);
        await WaitUntilAsync(() => log.Contains("DVD title reconciliation conflict"), TimeSpan.FromSeconds(4), "the conflict warning");

        var map = service.GetDvdTitleMapForTesting(Disc);
        Check(!map.ContainsKey(4) && !map.ContainsKey(5), "Both conflicting claims must be withdrawn, not overwritten");
        Check(map.Count == RegressionTruth.Count - 2, "Unrelated mappings must be kept");

        var refusal = await CaptureAsync<InvalidOperationException>(() => service.OpenDvdTitleAsync(Disc, 4));
        Check(refusal.Message.Contains("withdrawn"), "The refusal must explain the withdrawn mapping: " + refusal.Message);
    }

    // ----- helpers ----------------------------------------------------------------------

    private static void UseLoadedTitle(string duration, int chapters)
    {
        UseStub("normal");
        Environment.SetEnvironmentVariable("PARALLAX_DVD_STUB_DURATION", duration);
        Environment.SetEnvironmentVariable("PARALLAX_DVD_STUB_CHAPTERS", chapters.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static BluRayTitle Meta(int index, string duration, int chapters = 0) =>
        new(index, false, Duration(duration), chapters, SourceKind: DiscKind.Dvd);

    private static DvdEditionInfo Edition(int selector, string duration) => new(selector, Duration(duration), $"title {selector + 1}");

    private static TimeSpan Duration(string value) => TimeSpan.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    private static bool SameMap(IReadOnlyDictionary<int, int> actual, IReadOnlyDictionary<int, int> expected) =>
        actual.Count == expected.Count && expected.All(pair => actual.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static string Describe(DvdTitleMapResult result) => string.Join("; ", result.Outcomes
        .OrderBy(outcome => outcome.MetadataIndex)
        .Select(outcome => $"m{outcome.MetadataIndex + 1}:{outcome.Kind}{(outcome.Selector is int selector ? $"->{selector}" : string.Empty)}"));

    private static string Canonical(DvdTitleMapResult result) => string.Join("; ", result.Outcomes
        .OrderBy(outcome => outcome.MetadataIndex)
        .Select(outcome => $"{outcome.MetadataIndex}:{outcome.Kind}:{outcome.Selector}:{string.Join(",", outcome.Candidates.Select(candidate => candidate.Id))}:{outcome.Reason}"));

    private static string DescribeLoaded(DvdLoadedTitleMatch match) =>
        $"{match.Kind}:{match.Title?.Index}:{string.Join(",", match.Candidates.Select(candidate => candidate.Id))}:{match.Reason}";
}
