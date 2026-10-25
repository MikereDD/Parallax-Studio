using ParallaxStudio.Core.Enums;
using ParallaxStudio.Core.Models;
using ParallaxStudio.Core.Services;
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
        ("service: conflicting post-load evidence withdraws the mapping", ServiceConflictWithdrawsMappingAsync),
        ("d13: all eight titles map to the observed DVD titles", Sync(D13MapsAllEightTitles)),
        ("d13: titles 3/4 and the aggregate title 5 are resolved by disc calibration, and labelled so", Sync(D13CalibratedTitlesAreLabelled)),
        ("d13: competing assignment 3->4 / 4->3 is outside the calibrated ranges", Sync(D13SwappedAssignmentIsInconsistent)),
        ("d13: without calibration anchors, 3/4 stay ambiguous and 5 stays unmatched", Sync(D13WithoutAnchorsStaysUncertain)),
        ("d13: elimination alone does not resolve titles 3 and 4", Sync(D13EliminationIsNotEvidence)),
        ("d13: inconsistent anchors disable calibration", Sync(D13InconsistentAnchorsDisableCalibration)),
        ("d13: an extra DVD title inside a calibrated range keeps the title ambiguous", Sync(D13ExtraCandidateStaysAmbiguous)),
        ("d13: outcome is identical for every input order", Sync(D13IsOrderIndependent)),
        ("d13: loaded DVD title 4 verifies metadata 4 only with calibration", Sync(D13LoadedTitle4NeedsCalibration)),
        ("d13: loaded aggregate title verifies metadata 5 with calibration and chapters", Sync(D13LoadedAggregate)),
        ("service: d13 plays and verifies all eight titles, including metadata 6 -> DVD title 8", ServiceD13PlaysAllTitlesAsync),
        ("service: refusal is an OpticalTitleUnavailableException", ServiceRefusalTypeAsync)
    ];

    private static Func<Task> Sync(Action action) => () =>
    {
        action();
        return Task.CompletedTask;
    };

    // ----- full map -------------------------------------------------------------------

    // Synthetic disc: eight MakeMKV titles, libdvdnav selectors in a different order,
    // libdvdnav runtimes 6–7 s longer. Every title is directly unique. (The exact D13
    // runtimes observed on NETZACH are in D13Metadata/D13Editions below.)
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

    // ----- D13 (exact runtimes from the NETZACH log, 2026-10-25 15:35 and 15:39) --------

    private static readonly BluRayTitle[] D13Metadata =
    [
        Meta(0, "1:07:26", 13), Meta(1, "0:43:55", 13), Meta(2, "0:41:59", 13), Meta(3, "0:41:47", 13),
        Meta(4, "3:59:43", 61), Meta(5, "0:44:36", 13), Meta(6, "0:00:24", 3), Meta(7, "0:01:35", 3)
    ];

    // DVD titles 1–20 (selectors 0–19), including the twelve zero-length titles.
    private static readonly DvdEditionInfo[] D13Editions = new[]
    {
        "1:07:32", "0:44:00", "0:42:04", "0:41:53", "4:00:12", "0:00:00", "0:00:00", "0:44:43", "0:00:25", "0:00:00",
        "0:00:00", "0:00:00", "0:00:00", "0:00:00", "0:00:00", "0:00:00", "0:00:00", "0:00:00", "0:01:36", "0:00:00"
    }.Select((duration, selector) => Edition(selector, duration)).ToArray();

    // Metadata index -> DVD selector. 1/2/6/7/8 were matched on the baseline and are
    // confirmed by playback logs; 3 -> DVD title 3 is confirmed by the 14:18 playback log
    // (selector 2 loaded 42:04 / 13 chapters).
    private static readonly Dictionary<int, int> D13Truth = new()
    {
        [0] = 0, [1] = 1, [2] = 2, [3] = 3, [4] = 4, [5] = 7, [6] = 8, [7] = 18
    };

    private static void D13MapsAllEightTitles()
    {
        var result = DvdTitleMatcher.BuildFullMap(D13Metadata, D13Editions);
        Check(SameMap(result.Mapping, D13Truth), "D13 must map 8/8: " + Describe(result));
        Check(result.Mapping[5] == 7, "Metadata title 6 must stay DVD title 8");
        Check(result.Calibration is not null, "D13 has enough consistent anchors to calibrate");
    }

    private static void D13CalibratedTitlesAreLabelled()
    {
        var result = DvdTitleMatcher.BuildFullMap(D13Metadata, D13Editions);
        var calibrated = result.Outcomes.Where(outcome => outcome.Basis == DvdTitleMatchBasis.Calibrated).Select(outcome => outcome.MetadataIndex).OrderBy(index => index);
        Check(calibrated.SequenceEqual(new[] { 2, 3, 4 }), "Only titles 3, 4 and 5 need calibration: " + string.Join(",", calibrated));
        Check(result.Calibration!.AnchorMetadataIndexes.SequenceEqual(new[] { 0, 1, 5, 6, 7 }), "Anchors are the five direct matches");
        Check(result.OutcomeFor(4)!.Reason.Contains("disc-calibrated range"), "The aggregate match must say what evidence it rests on");
    }

    private static void D13SwappedAssignmentIsInconsistent()
    {
        // The only competing one-to-one assignment within 15 s is 3->DVD 4, 4->DVD 3.
        // It requires -6 s and +17 s offsets; the disc's own titles show +5..+7 s at this length.
        var calibration = DvdTitleMatcher.BuildFullMap(D13Metadata, D13Editions).Calibration!;
        Check(calibration.IsConsistent(Duration("0:41:59"), Duration("0:42:04")), "3 -> DVD 3 is consistent");
        Check(calibration.IsConsistent(Duration("0:41:47"), Duration("0:41:53")), "4 -> DVD 4 is consistent");
        Check(!calibration.IsConsistent(Duration("0:41:59"), Duration("0:41:53")), "3 -> DVD 4 (-6 s) is not");
        Check(!calibration.IsConsistent(Duration("0:41:47"), Duration("0:42:04")), "4 -> DVD 3 (+17 s) is not");
        Check(calibration.IsConsistent(Duration("3:59:43"), Duration("4:00:12")), "5 -> DVD 5 (+29 s at 4 h) is consistent");
    }

    private static void D13WithoutAnchorsStaysUncertain()
    {
        var result = DvdTitleMatcher.BuildFullMap(
            D13Metadata.Where(title => title.Index is 2 or 3 or 4).ToArray(),
            D13Editions);
        Check(result.Mapping.Count == 0 && result.Calibration is null, "No anchors, no calibration, no mapping: " + Describe(result));
        Check(result.OutcomeFor(2)!.Kind == DvdTitleMatchKind.Ambiguous && result.OutcomeFor(3)!.Kind == DvdTitleMatchKind.Ambiguous,
            "Titles 3 and 4 must stay ambiguous");
        Check(result.OutcomeFor(4)!.Kind == DvdTitleMatchKind.Unmatched, "Title 5 must stay unmatched under the fixed window");
        Check(result.CalibrationNote?.Contains("no disc calibration") == true, "The missing calibration must be explained");
    }

    private static void D13EliminationIsNotEvidence()
    {
        // Within 15 s, title 4 only fits DVD 4, so "elimination" would force 3 -> DVD 3. That
        // assumes title 4 really is on the DVD; it is not used as proof. With just two
        // anchors (one long) there is no calibration, so both stay ambiguous.
        var result = DvdTitleMatcher.BuildFullMap(
            D13Metadata.Where(title => title.Index is 0 or 2 or 3 or 6).ToArray(),
            D13Editions);
        Check(result.Calibration is null, "Two anchors with one long title must not calibrate");
        Check(!result.Mapping.ContainsKey(2) && !result.Mapping.ContainsKey(3), "3 and 4 must not be resolved by elimination: " + Describe(result));
    }

    private static void D13InconsistentAnchorsDisableCalibration()
    {
        // Make metadata 2's DVD title 14 s longer: still a direct match, but its ratio
        // (0.42–0.65 %) no longer agrees with metadata 1 (0.07–0.22 %).
        var editions = D13Editions.Select(edition => edition.Selector == 1 ? Edition(1, "0:44:09") : edition).ToArray();
        var result = DvdTitleMatcher.BuildFullMap(D13Metadata, editions);
        Check(result.Calibration is null && result.CalibrationNote?.Contains("disagree") == true, "Disagreeing anchors must disable calibration: " + result.CalibrationNote);
        Check(!result.Mapping.ContainsKey(2) && !result.Mapping.ContainsKey(3) && !result.Mapping.ContainsKey(4),
            "Without calibration, 3/4/5 stay unresolved: " + Describe(result));
    }

    private static void D13ExtraCandidateStaysAmbiguous()
    {
        // A second DVD title at 42:05 lies inside title 3's calibrated range.
        var editions = D13Editions.Append(Edition(20, "0:42:05")).ToArray();
        var result = DvdTitleMatcher.BuildFullMap(D13Metadata, editions);
        Check(result.OutcomeFor(2)!.Kind == DvdTitleMatchKind.Ambiguous && !result.Mapping.ContainsKey(2),
            "Two DVD titles in the calibrated range must stay ambiguous: " + Describe(result));
        Check(result.Mapping.TryGetValue(3, out var title4) && title4 == 3, "Title 4 is unaffected");
    }

    private static void D13IsOrderIndependent()
    {
        var expected = Canonical(DvdTitleMatcher.BuildFullMap(D13Metadata, D13Editions));
        var random = new Random(2018);
        for (var round = 0; round < 50; round++)
        {
            var actual = Canonical(DvdTitleMatcher.BuildFullMap(
                D13Metadata.OrderBy(_ => random.Next()).ToArray(),
                D13Editions.OrderBy(_ => random.Next()).ToArray()));
            Check(actual == expected, $"Round {round}: outcome depends on input order");
        }
    }

    private static void D13LoadedTitle4NeedsCalibration()
    {
        var calibration = DvdTitleMatcher.BuildFullMap(D13Metadata, D13Editions).Calibration;
        var without = DvdTitleMatcher.MatchLoadedTitle(D13Metadata, Duration("0:41:53"), 13);
        var with = DvdTitleMatcher.MatchLoadedTitle(D13Metadata, Duration("0:41:53"), 13, calibration);
        Check(without.Kind == DvdTitleMatchKind.Ambiguous, "41:53 is 6 s from both 41:59 and 41:47");
        Check(with.Kind == DvdTitleMatchKind.Unique && with.Title?.Index == 3, $"Calibration must identify metadata 4: {with.Kind}: {with.Reason}");
    }

    private static void D13LoadedAggregate()
    {
        var calibration = DvdTitleMatcher.BuildFullMap(D13Metadata, D13Editions).Calibration;
        var match = DvdTitleMatcher.MatchLoadedTitle(D13Metadata, Duration("4:00:12"), 61, calibration);
        Check(match.Kind == DvdTitleMatchKind.Unique && match.Title?.Index == 4, $"The aggregate must verify as metadata 5: {match.Kind}: {match.Reason}");
        var wrongChapters = DvdTitleMatcher.MatchLoadedTitle(D13Metadata, Duration("4:00:12"), 13, calibration);
        Check(wrongChapters.Kind == DvdTitleMatchKind.Unmatched, "A 13-chapter program is not the 61-chapter aggregate");
    }

    private static async Task ServiceD13PlaysAllTitlesAsync()
    {
        var log = new TestLogger();
        await using var service = NewService(log);
        await service.SeedDvdTitleMapForTestingAsync(Disc, D13Metadata, D13Editions);
        Check(log.Contains("Full DVD title map built with 8 confident matches"), "The production apply path must map 8/8");
        Check(log.Contains("metadata 6->DVD title 8,") && log.Contains("metadata 3->DVD title 3 (calibrated)"), "Calibrated matches must be labelled in the log");
        Check(log.Contains("DVD title-map calibration: DVD runtime = metadata ×"), "The calibration must be logged");

        foreach (var (metadataIndex, selector) in D13Truth.OrderBy(pair => pair.Key))
        {
            var title = D13Metadata.Single(item => item.Index == metadataIndex);
            var edition = D13Editions.Single(item => item.Selector == selector);
            UseLoadedTitle(edition.Duration.ToString("c", System.Globalization.CultureInfo.InvariantCulture), title.ChapterCount);
            var reconciledBefore = log.Count("DVD title reconciled");
            await service.OpenDvdTitleAsync(Disc, metadataIndex);
            var pid = log.StubPids().Last();
            Check(log.Contains($"STUB_TITLE {pid} {selector}"), $"Metadata {metadataIndex + 1} must start DVD selector {selector}");
            await WaitUntilAsync(() => log.Count("DVD title reconciled") > reconciledBefore, TimeSpan.FromSeconds(4), $"metadata {metadataIndex + 1} to verify");
            Check(log.Contains($"matched metadata title {metadataIndex + 1} ("), $"Metadata {metadataIndex + 1} must verify as itself");
        }

        Check(SameMap(service.GetDvdTitleMapForTesting(Disc), D13Truth), "Verification must leave the map unchanged");
        Check(!log.Contains("could not verify") && !log.Contains("reconciliation conflict"), "No title may be unverified or conflicting");
        log.AssertNoErrors();
    }

    private static async Task ServiceRefusalTypeAsync()
    {
        var log = new TestLogger();
        await using var service = NewService(log);
        await service.SeedDvdTitleMapForTestingAsync(
            Disc,
            D13Metadata.Where(title => title.Index is 2 or 3).ToArray(),
            D13Editions);
        UseStub("normal");
        var refusal = await CaptureAsync<OpticalTitleUnavailableException>(() => service.OpenDvdTitleAsync(Disc, 2));
        Check(refusal.Message.Contains("ambiguous"), "The refusal must carry the reason: " + refusal.Message);
        Check(log.StubPids().Count == 0, "No host may start for a refused title");
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
