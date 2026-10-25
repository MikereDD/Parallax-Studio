using System.Globalization;
using ParallaxStudio.Core.Models;

namespace ParallaxStudio.Infrastructure.Playback;

internal enum DvdTitleMatchKind
{
    /// <summary>Exactly one counterpart is consistent with the evidence, in both directions.</summary>
    Unique,
    /// <summary>More than one counterpart is consistent; the evidence cannot tell them apart.</summary>
    Ambiguous,
    /// <summary>No counterpart is consistent with the evidence.</summary>
    Unmatched
}

/// <summary>Which evidence established (or failed to establish) a match.</summary>
internal enum DvdTitleMatchBasis
{
    /// <summary>The fixed <see cref="DvdTitleMatcher.RuntimeWindow"/> around the metadata runtime.</summary>
    Direct,
    /// <summary>The runtime relationship measured on this disc's own directly matched titles.</summary>
    Calibrated
}

/// <summary>A consistent counterpart: a DVD selector (full map) or a metadata title index (loaded title).</summary>
internal sealed record DvdTitleCandidate(int Id, TimeSpan Duration, double DeltaSeconds, int ChapterCount = 0);

internal sealed record DvdTitleMatchOutcome(
    int MetadataIndex,
    DvdTitleMatchKind Kind,
    int? Selector,
    IReadOnlyList<DvdTitleCandidate> Candidates,
    string Reason,
    DvdTitleMatchBasis Basis = DvdTitleMatchBasis.Direct);

internal sealed record DvdTitleMapResult(
    IReadOnlyDictionary<int, int> Mapping,
    IReadOnlyList<DvdTitleMatchOutcome> Outcomes,
    DvdRuntimeCalibration? Calibration = null,
    string? CalibrationNote = null)
{
    public DvdTitleMatchOutcome? OutcomeFor(int metadataIndex) =>
        Outcomes.FirstOrDefault(outcome => outcome.MetadataIndex == metadataIndex);
}

internal sealed record DvdLoadedTitleMatch(
    DvdTitleMatchKind Kind,
    BluRayTitle? Title,
    IReadOnlyList<DvdTitleCandidate> Candidates,
    string Reason);

/// <summary>
/// The runtime relationship between MakeMKV and libdvdnav measured on one disc. The model is
/// <c>dvd = metadata × (1 + r) ± <see cref="Rounding"/></c> for every r in
/// [<see cref="MinRatio"/>, <see cref="MaxRatio"/>]. That interval is the set of ratios
/// consistent with <em>every</em> anchor (a directly and uniquely matched title) at once.
/// The D13 disc shows why a proportional model is needed: +1 s at 0:24, +5 to +7 s at
/// 42–67 min, and +29 s at 4:00.
/// </summary>
internal sealed record DvdRuntimeCalibration(
    double MinRatio,
    double MaxRatio,
    TimeSpan Rounding,
    IReadOnlyList<int> AnchorMetadataIndexes)
{
    public (double LowSeconds, double HighSeconds) PredictDvdSeconds(TimeSpan metadataDuration)
    {
        var seconds = metadataDuration.TotalSeconds;
        return (seconds * (1 + MinRatio) - Rounding.TotalSeconds, seconds * (1 + MaxRatio) + Rounding.TotalSeconds);
    }

    public bool IsConsistent(TimeSpan metadataDuration, TimeSpan dvdDuration)
    {
        var (low, high) = PredictDvdSeconds(metadataDuration);
        var dvd = dvdDuration.TotalSeconds;
        return dvd >= low && dvd <= high;
    }

    public string DescribeRange(TimeSpan metadataDuration)
    {
        var (low, high) = PredictDvdSeconds(metadataDuration);
        return $"{DvdTitleMatcher.Format(TimeSpan.FromSeconds(Math.Max(0, Math.Ceiling(low))))}–{DvdTitleMatcher.Format(TimeSpan.FromSeconds(Math.Floor(high)))}";
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture,
            $"DVD runtime = metadata × (1 + {MinRatio * 100:0.000}%…{MaxRatio * 100:0.000}%) ± {Rounding.TotalSeconds:0} s, " +
            $"from metadata titles {string.Join(", ", AnchorMetadataIndexes.Select(index => index + 1))}");
}

/// <summary>
/// Matches MakeMKV title metadata to libdvdnav titles (DVD selectors) by runtime, and a
/// loaded DVD title back to metadata by runtime and chapter count.
///
/// A match is accepted only when it is the <em>only</em> consistent candidate in both
/// directions. The metadata title has exactly one consistent DVD title, and that DVD title
/// has exactly one consistent metadata title. "Closest" is never evidence, and index or
/// selector order is never used, so results do not depend on input order and nonsequential
/// authoring is preserved. Elimination (a forced one-to-one assignment) is not used as
/// evidence either. It assumes every metadata title has a counterpart, which is exactly the
/// thing being established.
///
/// Matching runs in two stages:
/// <list type="number">
/// <item><b>Direct.</b> Consistency means the runtimes differ by at most
/// <see cref="RuntimeWindow"/>. The unique matches become anchors.</item>
/// <item><b>Calibrated.</b> Used only if stage 1 leaves titles unresolved and the anchors
/// agree on a single proportional runtime relationship (<see cref="DvdRuntimeCalibration"/>).
/// The remaining titles are then judged against the runtime range that relationship predicts
/// for them. This is measured evidence from the disc itself, it is reported as such, and it
/// is verified again by post-load reconciliation.</item>
/// </list>
/// </summary>
internal static class DvdTitleMatcher
{
    /// <summary>
    /// Stage-1 window: a fixed runtime difference treated as the same program. It is not a
    /// universal bound; D13's 4-hour aggregate differs by 29 s. It selects anchors, and it is
    /// the fallback when no disc calibration is possible.
    /// </summary>
    public static readonly TimeSpan RuntimeWindow = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Both tools report whole seconds (±1 s each, so ±2 s together), plus 1 s of margin.
    /// This is the only absolute allowance in the calibrated model.
    /// </summary>
    public static readonly TimeSpan CalibrationRounding = TimeSpan.FromSeconds(3);

    /// <summary>Calibration needs at least this many anchors…</summary>
    public const int MinimumCalibrationAnchors = 3;

    /// <summary>…of which at least this many are long enough to constrain the ratio.</summary>
    public const int MinimumLongCalibrationAnchors = 2;

    /// <summary>
    /// An anchor this long constrains the ratio to within ±0.5 % (3 s / 10 min); shorter
    /// anchors are still checked for consistency but say little about the ratio.
    /// </summary>
    public static readonly TimeSpan LongAnchorDuration = TimeSpan.FromMinutes(10);

    /// <summary>
    /// MakeMKV and libdvdnav can disagree on a trailing chapter, so a difference of one is
    /// consistent. Chapter counts only exclude candidates; a closer count is never treated as
    /// proof, because an off-by-one count is equally consistent.
    /// </summary>
    public const int ChapterTolerance = 1;

    private static readonly TimeSpan AggregateTitleLimit = TimeSpan.FromHours(12);

    /// <summary>Builds the pre-playback metadata → DVD selector map from runtimes.</summary>
    public static DvdTitleMapResult BuildFullMap(
        IReadOnlyList<BluRayTitle> metadataTitles,
        IReadOnlyList<DvdEditionInfo> editions)
    {
        var metadata = metadataTitles
            .Where(title => IsPlayableDuration(title.Duration))
            .GroupBy(title => title.Index)
            .Select(group => group.First())
            .OrderBy(title => title.Index)
            .ToArray();
        var selectors = editions
            .Where(edition => edition.Selector >= 0 && IsPlayableDuration(edition.Duration))
            .GroupBy(edition => edition.Selector)
            .Select(group => group.First())
            .OrderBy(edition => edition.Selector)
            .ToArray();

        var direct = MatchAll(
            metadata,
            selectors,
            (title, edition) => Delta(title.Duration, edition.Duration) <= RuntimeWindow.TotalSeconds,
            _ => $"within {RuntimeWindow.TotalSeconds:0} s",
            DvdTitleMatchBasis.Direct);

        if (direct.All(outcome => outcome.Kind == DvdTitleMatchKind.Unique))
            return Result(direct, null, null);

        var calibration = Calibrate(metadata, selectors, direct, out var note);
        if (calibration is null)
            return Result(direct, null, note);

        var calibrated = MatchAll(
            metadata,
            selectors,
            (title, edition) => calibration.IsConsistent(title.Duration, edition.Duration),
            title => $"within the disc-calibrated range {calibration.DescribeRange(title.Duration)}",
            DvdTitleMatchBasis.Calibrated);

        // Direct matches stand (they are the anchors, and every anchor lies inside its own
        // calibrated range by construction). The rest take the calibrated outcome: a unique
        // calibrated match, or the calibrated description of why it is still unresolved.
        var outcomes = direct
            .Select(outcome => outcome.Kind == DvdTitleMatchKind.Unique
                ? outcome
                : calibrated.First(candidate => candidate.MetadataIndex == outcome.MetadataIndex))
            .ToArray();
        return Result(outcomes, calibration, null);
    }

    /// <summary>
    /// Identifies which metadata title a loaded DVD title is, from the runtime and chapter
    /// count reported by the playback host. With a disc calibration, runtimes are judged
    /// against each title's calibrated range instead of the fixed window.
    /// </summary>
    public static DvdLoadedTitleMatch MatchLoadedTitle(
        IReadOnlyList<BluRayTitle> metadataTitles,
        TimeSpan actualDuration,
        int actualChapterCount,
        DvdRuntimeCalibration? calibration = null)
    {
        if (!IsPlayableDuration(actualDuration))
            return new DvdLoadedTitleMatch(DvdTitleMatchKind.Unmatched, null, [], "the loaded title reported no usable runtime");

        bool RuntimeConsistent(BluRayTitle title) => calibration is null
            ? Delta(title.Duration, actualDuration) <= RuntimeWindow.TotalSeconds
            : calibration.IsConsistent(title.Duration, actualDuration);

        var consistent = metadataTitles
            .Where(title => IsPlayableDuration(title.Duration))
            .GroupBy(title => title.Index)
            .Select(group => group.First())
            .Where(RuntimeConsistent)
            .Where(title => actualChapterCount <= 0 || title.ChapterCount <= 0 ||
                Math.Abs(title.ChapterCount - actualChapterCount) <= ChapterTolerance)
            .OrderBy(title => Delta(title.Duration, actualDuration))
            .ThenBy(title => title.Index)
            .ToArray();
        var candidates = consistent
            .Select(title => new DvdTitleCandidate(title.Index, title.Duration, Delta(title.Duration, actualDuration), title.ChapterCount))
            .ToArray();
        var window = calibration is null ? $"within {RuntimeWindow.TotalSeconds:0} s" : "within the disc-calibrated range";

        return consistent.Length switch
        {
            0 => new DvdLoadedTitleMatch(
                DvdTitleMatchKind.Unmatched,
                null,
                candidates,
                $"no metadata title {window} and ±{ChapterTolerance} chapter of {Format(actualDuration)}/{actualChapterCount} chapters"),
            1 => new DvdLoadedTitleMatch(
                DvdTitleMatchKind.Unique,
                consistent[0],
                candidates,
                $"metadata title {consistent[0].DisplayNumber} is the only consistent title {window}"),
            _ => new DvdLoadedTitleMatch(
                DvdTitleMatchKind.Ambiguous,
                null,
                candidates,
                $"{consistent.Length} metadata titles are consistent with {Format(actualDuration)}/{actualChapterCount} chapters {window}: {DescribeMetadata(candidates)}")
        };
    }

    private static DvdTitleMapResult Result(IReadOnlyList<DvdTitleMatchOutcome> outcomes, DvdRuntimeCalibration? calibration, string? note) =>
        new(
            outcomes.Where(outcome => outcome.Kind == DvdTitleMatchKind.Unique)
                .ToDictionary(outcome => outcome.MetadataIndex, outcome => outcome.Selector!.Value),
            outcomes,
            calibration,
            note);

    /// <summary>
    /// Bidirectional uniqueness under <paramref name="consistent"/>: a metadata title maps
    /// only if it has exactly one consistent DVD title and that DVD title has exactly one
    /// consistent metadata title.
    /// </summary>
    private static DvdTitleMatchOutcome[] MatchAll(
        BluRayTitle[] metadata,
        DvdEditionInfo[] selectors,
        Func<BluRayTitle, DvdEditionInfo, bool> consistent,
        Func<BluRayTitle, string> window,
        DvdTitleMatchBasis basis)
    {
        var outcomes = new List<DvdTitleMatchOutcome>();
        foreach (var title in metadata)
        {
            var forward = selectors
                .Where(edition => consistent(title, edition))
                .Select(edition => new DvdTitleCandidate(edition.Selector, edition.Duration, Delta(title.Duration, edition.Duration)))
                .OrderBy(candidate => candidate.DeltaSeconds)
                .ThenBy(candidate => candidate.Id)
                .ToArray();

            if (forward.Length == 0)
            {
                var nearest = selectors
                    .Select(edition => new DvdTitleCandidate(edition.Selector, edition.Duration, Delta(title.Duration, edition.Duration)))
                    .OrderBy(candidate => candidate.DeltaSeconds)
                    .ThenBy(candidate => candidate.Id)
                    .FirstOrDefault();
                outcomes.Add(new DvdTitleMatchOutcome(
                    title.Index,
                    DvdTitleMatchKind.Unmatched,
                    null,
                    [],
                    nearest is null
                        ? "the DVD exposed no playable titles"
                        : $"no DVD title {window(title)} (nearest: DVD title {nearest.Id + 1} at {Format(nearest.Duration)}, {nearest.DeltaSeconds:0} s away)",
                    basis));
                continue;
            }

            if (forward.Length > 1)
            {
                outcomes.Add(new DvdTitleMatchOutcome(
                    title.Index,
                    DvdTitleMatchKind.Ambiguous,
                    null,
                    forward,
                    $"{forward.Length} DVD titles are {window(title)}: {DescribeSelectors(forward)}",
                    basis));
                continue;
            }

            var selected = forward[0];
            var selectedEdition = selectors.First(edition => edition.Selector == selected.Id);
            var reverse = metadata
                .Where(other => consistent(other, selectedEdition))
                .Select(other => new DvdTitleCandidate(other.Index, other.Duration, Delta(other.Duration, selected.Duration), other.ChapterCount))
                .OrderBy(candidate => candidate.DeltaSeconds)
                .ThenBy(candidate => candidate.Id)
                .ToArray();

            if (reverse.Length > 1)
            {
                outcomes.Add(new DvdTitleMatchOutcome(
                    title.Index,
                    DvdTitleMatchKind.Ambiguous,
                    null,
                    forward,
                    $"DVD title {selected.Id + 1} ({Format(selected.Duration)}) is also consistent with {DescribeMetadata(reverse.Where(candidate => candidate.Id != title.Index))}",
                    basis));
                continue;
            }

            outcomes.Add(new DvdTitleMatchOutcome(
                title.Index,
                DvdTitleMatchKind.Unique,
                selected.Id,
                forward,
                $"DVD title {selected.Id + 1} ({Format(selected.Duration)}, {selected.DeltaSeconds:0} s) is the only title {window(title)}",
                basis));
        }

        return outcomes.ToArray();
    }

    /// <summary>
    /// Finds the ratios r for which every direct anchor satisfies
    /// |dvd − metadata × (1 + r)| ≤ <see cref="CalibrationRounding"/>. Returns null, with the
    /// reason, if there are too few anchors or no single ratio explains all of them.
    /// </summary>
    private static DvdRuntimeCalibration? Calibrate(
        BluRayTitle[] metadata,
        DvdEditionInfo[] selectors,
        IReadOnlyList<DvdTitleMatchOutcome> direct,
        out string note)
    {
        var anchors = direct
            .Where(outcome => outcome.Kind == DvdTitleMatchKind.Unique)
            .Select(outcome => (
                Metadata: metadata.First(title => title.Index == outcome.MetadataIndex),
                Dvd: selectors.First(edition => edition.Selector == outcome.Selector)))
            .ToArray();
        var longAnchors = anchors.Count(anchor => anchor.Metadata.Duration >= LongAnchorDuration);

        if (anchors.Length < MinimumCalibrationAnchors || longAnchors < MinimumLongCalibrationAnchors)
        {
            note = $"no disc calibration: {anchors.Length} direct match(es), {longAnchors} of at least {LongAnchorDuration.TotalMinutes:0} min " +
                $"(needs {MinimumCalibrationAnchors} and {MinimumLongCalibrationAnchors})";
            return null;
        }

        var rounding = CalibrationRounding.TotalSeconds;
        var minRatio = double.NegativeInfinity;
        var maxRatio = double.PositiveInfinity;
        foreach (var (title, dvd) in anchors)
        {
            var seconds = title.Duration.TotalSeconds;
            var delta = dvd.Duration.TotalSeconds - seconds;
            minRatio = Math.Max(minRatio, (delta - rounding) / seconds);
            maxRatio = Math.Min(maxRatio, (delta + rounding) / seconds);
        }

        var anchorText = string.Join(", ", anchors.Select(anchor =>
            $"{anchor.Metadata.DisplayNumber} ({Format(anchor.Metadata.Duration)}→{Format(anchor.Dvd.Duration)})"));
        if (minRatio > maxRatio)
        {
            note = $"no disc calibration: the direct matches disagree on the runtime relationship ({anchorText})";
            return null;
        }

        note = string.Empty;
        return new DvdRuntimeCalibration(
            minRatio,
            maxRatio,
            CalibrationRounding,
            anchors.Select(anchor => anchor.Metadata.Index).OrderBy(index => index).ToArray());
    }

    private static bool IsPlayableDuration(TimeSpan duration) =>
        duration > TimeSpan.Zero && duration <= AggregateTitleLimit;

    private static double Delta(TimeSpan left, TimeSpan right) => Math.Abs((left - right).TotalSeconds);

    private static string DescribeSelectors(IEnumerable<DvdTitleCandidate> candidates) =>
        string.Join(", ", candidates.Select(candidate =>
            $"DVD title {candidate.Id + 1} ({Format(candidate.Duration)}, {candidate.DeltaSeconds:0} s)"));

    private static string DescribeMetadata(IEnumerable<DvdTitleCandidate> candidates) =>
        string.Join(", ", candidates.Select(candidate =>
            $"metadata title {candidate.Id + 1} ({Format(candidate.Duration)}{(candidate.ChapterCount > 0 ? $", {candidate.ChapterCount} ch" : string.Empty)}, {candidate.DeltaSeconds:0} s)"));

    internal static string Format(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);
}
