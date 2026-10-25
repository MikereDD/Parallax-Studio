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

/// <summary>A consistent counterpart: a DVD selector (full map) or a metadata title index (loaded title).</summary>
internal sealed record DvdTitleCandidate(int Id, TimeSpan Duration, double DeltaSeconds, int ChapterCount = 0);

internal sealed record DvdTitleMatchOutcome(
    int MetadataIndex,
    DvdTitleMatchKind Kind,
    int? Selector,
    IReadOnlyList<DvdTitleCandidate> Candidates,
    string Reason);

internal sealed record DvdTitleMapResult(
    IReadOnlyDictionary<int, int> Mapping,
    IReadOnlyList<DvdTitleMatchOutcome> Outcomes)
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
/// Matches MakeMKV title metadata to libdvdnav titles (DVD selectors) by runtime, and a
/// loaded DVD title back to metadata by runtime and chapter count.
///
/// The two tools measure the same program with a small discrepancy (the regression disc
/// shows MakeMKV 44:36 vs libdvdnav 44:43 and 1:07:26 vs 1:07:32). With such noise, "the
/// closest candidate" is not evidence: when two titles are a few seconds apart, a consistent
/// offset makes the wrong one closer. A match is therefore accepted only when it is the
/// <em>only</em> candidate within <see cref="RuntimeWindow"/>, in both directions: the metadata
/// title has exactly one consistent DVD title, and that DVD title has exactly one consistent
/// metadata title. If the true counterpart is within the window (the window bounds the
/// measurement discrepancy), a unique candidate must be the true one. Anything else is
/// reported as ambiguous or unmatched. Index and selector order are never used, so the
/// result is identical for any input order and nonsequential authoring is preserved.
/// </summary>
internal static class DvdTitleMatcher
{
    /// <summary>
    /// Largest MakeMKV/libdvdnav runtime discrepancy treated as the same program. About twice
    /// the 6–7 s discrepancy observed on the regression disc. Titles whose runtimes are closer
    /// together than this cannot be told apart by runtime alone.
    /// </summary>
    public static readonly TimeSpan RuntimeWindow = TimeSpan.FromSeconds(15);

    /// <summary>
    /// MakeMKV and libdvdnav can disagree on a trailing chapter, so a difference of one is
    /// consistent. Chapter counts only exclude candidates; a closer count is never treated as
    /// proof, because an off-by-one count is equally consistent.
    /// </summary>
    public const int ChapterTolerance = 1;

    private static readonly TimeSpan AggregateTitleLimit = TimeSpan.FromHours(12);

    /// <summary>Builds the pre-playback metadata → DVD selector map from runtimes only.</summary>
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

        var mapping = new Dictionary<int, int>();
        var outcomes = new List<DvdTitleMatchOutcome>();

        foreach (var title in metadata)
        {
            var forward = selectors
                .Where(edition => Delta(title.Duration, edition.Duration) <= RuntimeWindow.TotalSeconds)
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
                        : $"no DVD title within {RuntimeWindow.TotalSeconds:0} s (nearest: DVD title {nearest.Id + 1} at {Format(nearest.Duration)}, {nearest.DeltaSeconds:0} s away)"));
                continue;
            }

            if (forward.Length > 1)
            {
                outcomes.Add(new DvdTitleMatchOutcome(
                    title.Index,
                    DvdTitleMatchKind.Ambiguous,
                    null,
                    forward,
                    $"{forward.Length} DVD titles are within {RuntimeWindow.TotalSeconds:0} s: {DescribeSelectors(forward)}"));
                continue;
            }

            var selected = forward[0];
            var reverse = metadata
                .Where(other => Delta(other.Duration, selected.Duration) <= RuntimeWindow.TotalSeconds)
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
                    $"DVD title {selected.Id + 1} ({Format(selected.Duration)}) is also within {RuntimeWindow.TotalSeconds:0} s of {DescribeMetadata(reverse.Where(candidate => candidate.Id != title.Index))}"));
                continue;
            }

            mapping[title.Index] = selected.Id;
            outcomes.Add(new DvdTitleMatchOutcome(
                title.Index,
                DvdTitleMatchKind.Unique,
                selected.Id,
                forward,
                $"DVD title {selected.Id + 1} ({Format(selected.Duration)}, {selected.DeltaSeconds:0} s) is the only title within {RuntimeWindow.TotalSeconds:0} s"));
        }

        return new DvdTitleMapResult(mapping, outcomes);
    }

    /// <summary>
    /// Identifies which metadata title a loaded DVD title is, from the runtime and chapter
    /// count reported by the playback host.
    /// </summary>
    public static DvdLoadedTitleMatch MatchLoadedTitle(
        IReadOnlyList<BluRayTitle> metadataTitles,
        TimeSpan actualDuration,
        int actualChapterCount)
    {
        if (!IsPlayableDuration(actualDuration))
            return new DvdLoadedTitleMatch(DvdTitleMatchKind.Unmatched, null, [], "the loaded title reported no usable runtime");

        var consistent = metadataTitles
            .Where(title => IsPlayableDuration(title.Duration))
            .GroupBy(title => title.Index)
            .Select(group => group.First())
            .Where(title => Delta(title.Duration, actualDuration) <= RuntimeWindow.TotalSeconds)
            .Where(title => actualChapterCount <= 0 || title.ChapterCount <= 0 ||
                Math.Abs(title.ChapterCount - actualChapterCount) <= ChapterTolerance)
            .OrderBy(title => Delta(title.Duration, actualDuration))
            .ThenBy(title => title.Index)
            .ToArray();
        var candidates = consistent
            .Select(title => new DvdTitleCandidate(title.Index, title.Duration, Delta(title.Duration, actualDuration), title.ChapterCount))
            .ToArray();

        return consistent.Length switch
        {
            0 => new DvdLoadedTitleMatch(
                DvdTitleMatchKind.Unmatched,
                null,
                candidates,
                $"no metadata title within {RuntimeWindow.TotalSeconds:0} s and ±{ChapterTolerance} chapter of {Format(actualDuration)}/{actualChapterCount} chapters"),
            1 => new DvdLoadedTitleMatch(
                DvdTitleMatchKind.Unique,
                consistent[0],
                candidates,
                $"metadata title {consistent[0].DisplayNumber} is the only consistent title"),
            _ => new DvdLoadedTitleMatch(
                DvdTitleMatchKind.Ambiguous,
                null,
                candidates,
                $"{consistent.Length} metadata titles are consistent with {Format(actualDuration)}/{actualChapterCount} chapters: {DescribeMetadata(candidates)}")
        };
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
