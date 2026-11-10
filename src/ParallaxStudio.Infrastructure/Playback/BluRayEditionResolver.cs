using System.Globalization;
using System.Text.RegularExpressions;
using ParallaxStudio.Core.Models;

namespace ParallaxStudio.Infrastructure.Playback;

/// <summary>
/// One libbluray title as mpv exposes it through <c>edition-list</c> after a Blu-ray
/// source is opened. <see cref="Selector"/> is the zero-based libbluray title index
/// (the value <c>bd://N</c> accepts); <see cref="Title"/> is the raw mpv edition label,
/// for example <c>title: 92 (01:51:20.716) (00004.mpls)</c>.
/// </summary>
internal sealed record BluRayEditionInfo(int Selector, TimeSpan Duration, string? Playlist, string Title);

internal enum BluRayEditionResolutionKind
{
    Resolved,
    Refused
}

/// <summary>
/// Outcome of matching a MakeMKV-discovered title to a libbluray edition. A refusal is
/// not an error condition; it is the refusal-over-guess result when the evidence is
/// missing, conflicting, or ambiguous.
/// </summary>
internal sealed record BluRayEditionResolution(BluRayEditionResolutionKind Kind, int Selector, string Detail)
{
    public bool IsResolved => Kind == BluRayEditionResolutionKind.Resolved;

    public static BluRayEditionResolution Resolved(int selector, string detail) =>
        new(BluRayEditionResolutionKind.Resolved, selector, detail);

    public static BluRayEditionResolution Refused(string detail) =>
        new(BluRayEditionResolutionKind.Refused, -1, detail);
}

/// <summary>
/// Maps MakeMKV Blu-ray titles onto libbluray editions by evidence, never by ordinal
/// position: MakeMKV's filtered enumeration and libbluray's title list are known to
/// diverge (a 38-title MakeMKV disc exposed 118 libbluray editions, with the main
/// feature at MakeMKV index 1 but libbluray index 91). The identity preference order
/// is exact playlist filename, playlist plus runtime, unique runtime within
/// <see cref="DurationTolerance"/>; anything else refuses.
/// </summary>
internal static class BluRayEditionResolver
{
    /// <summary>
    /// Runtime agreement window between MakeMKV's TINFO duration and libbluray's
    /// playlist duration. Mirrors the ±3 s allowance the DVD title map uses.
    /// </summary>
    public static readonly TimeSpan DurationTolerance = TimeSpan.FromSeconds(3);

    public static BluRayEditionResolution Resolve(BluRayTitle requested, IReadOnlyList<BluRayEditionInfo> editions)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(editions);

        if (editions.Count == 0)
            return BluRayEditionResolution.Refused("libbluray reported no playable editions for the disc");

        var playlist = requested.Playlist?.Trim();
        if (!string.IsNullOrWhiteSpace(playlist))
        {
            var playlistMatches = editions
                .Where(edition => string.Equals(edition.Playlist, playlist, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (playlistMatches.Length == 1)
            {
                var match = playlistMatches[0];
                if (!WithinTolerance(requested.Duration, match.Duration))
                {
                    return BluRayEditionResolution.Refused(
                        $"playlist {playlist} matched only libbluray edition {match.Selector} (title {match.Selector + 1}), " +
                        $"but its runtime {Format(match.Duration)} conflicts with the MakeMKV runtime {Format(requested.Duration)}");
                }

                return BluRayEditionResolution.Resolved(
                    match.Selector,
                    $"exact playlist {playlist} match (edition {match.Selector}, runtime {Format(match.Duration)})");
            }

            if (playlistMatches.Length > 1)
            {
                var near = playlistMatches
                    .Where(edition => WithinTolerance(requested.Duration, edition.Duration))
                    .ToArray();
                if (near.Length == 1)
                {
                    return BluRayEditionResolution.Resolved(
                        near[0].Selector,
                        $"playlist {playlist} shared by {playlistMatches.Length} editions, disambiguated by runtime " +
                        $"{Format(requested.Duration)} (edition {near[0].Selector})");
                }

                return BluRayEditionResolution.Refused(
                    $"playlist {playlist} is carried by {playlistMatches.Length} libbluray editions " +
                    $"({DescribeSelectors(playlistMatches)}) and runtime cannot disambiguate them");
            }

            if (editions.Any(edition => !string.IsNullOrWhiteSpace(edition.Playlist)))
            {
                return BluRayEditionResolution.Refused(
                    $"no libbluray edition carries playlist {playlist}");
            }

            // No edition exposes playlist identity at all; fall through to runtime evidence.
        }

        if (requested.Duration <= TimeSpan.Zero)
        {
            return BluRayEditionResolution.Refused(
                string.IsNullOrWhiteSpace(playlist)
                    ? "MakeMKV reported neither a playlist nor a runtime for the title"
                    : $"no edition exposes playlist identity and MakeMKV reported no runtime for playlist {playlist}");
        }

        var runtimeCandidates = editions
            .Where(edition => edition.Duration > TimeSpan.Zero && WithinTolerance(requested.Duration, edition.Duration))
            .ToArray();

        return runtimeCandidates.Length switch
        {
            1 => BluRayEditionResolution.Resolved(
                runtimeCandidates[0].Selector,
                $"unique runtime match within {DurationTolerance.TotalSeconds:0} s " +
                $"(edition {runtimeCandidates[0].Selector}, runtime {Format(runtimeCandidates[0].Duration)})"),
            0 => BluRayEditionResolution.Refused(
                $"no libbluray edition runtime is within {DurationTolerance.TotalSeconds:0} s of {Format(requested.Duration)}"),
            _ => BluRayEditionResolution.Refused(
                $"{runtimeCandidates.Length} libbluray editions ({DescribeSelectors(runtimeCandidates)}) are within " +
                $"{DurationTolerance.TotalSeconds:0} s of {Format(requested.Duration)}"),
        };
    }

    /// <summary>
    /// Extracts runtime and playlist identity from an mpv edition label. Handles the
    /// Blu-ray form <c>title: 92 (01:51:20.716) (00004.mpls)</c>, the DVD form
    /// <c>title: 1 (01:07:32.900)</c>, and bare labels such as <c>Disc Menu</c>.
    /// </summary>
    public static (TimeSpan Duration, string? Playlist) ParseEditionTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return (TimeSpan.Zero, null);

        var duration = TimeSpan.Zero;
        foreach (Match group in Regex.Matches(title, @"\(([^()]*)\)"))
        {
            if (TimeSpan.TryParse(group.Groups[1].Value.Trim(), CultureInfo.InvariantCulture, out var parsed))
            {
                duration = parsed;
                break;
            }
        }

        var playlistMatch = Regex.Match(title, @"\b(\w+\.mpls)\b", RegexOptions.IgnoreCase);
        return (duration, playlistMatch.Success ? playlistMatch.Groups[1].Value : null);
    }

    private static bool WithinTolerance(TimeSpan requested, TimeSpan edition) =>
        requested <= TimeSpan.Zero || edition <= TimeSpan.Zero ||
        (requested - edition).Duration() <= DurationTolerance;

    private static string DescribeSelectors(IEnumerable<BluRayEditionInfo> editions) =>
        string.Join(", ", editions.Select(edition => $"edition {edition.Selector}"));

    private static string Format(TimeSpan duration) =>
        duration <= TimeSpan.Zero ? "unknown runtime" : duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);
}
