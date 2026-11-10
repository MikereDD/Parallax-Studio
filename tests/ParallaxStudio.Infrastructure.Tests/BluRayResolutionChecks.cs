using ParallaxStudio.Core.Models;
using ParallaxStudio.Infrastructure.Playback;

namespace ParallaxStudio.Infrastructure.Tests;

/// <summary>
/// Phase 4: Blu-ray indexed-title playback must resolve MakeMKV titles to libbluray
/// editions by evidence (playlist filename, then runtime), never by ordinal position.
/// Every check calls the production BluRayEditionResolver; no libmpv or optical drive
/// is needed. The divergent layout mirrors The Road (38 MakeMKV titles, 118 libbluray
/// editions, main feature at MakeMKV index 1 / libbluray index 91) observed physically.
/// </summary>
internal static partial class Program
{
    private static (string Name, Func<Task> Run)[] BluRayResolutionChecks =>
    [
        ("bluray: edition title parsing extracts runtime and playlist", Sync(EditionTitleParsing)),
        ("bluray: divergent enumeration resolves by playlist, never by raw index", Sync(DivergentEnumerationResolvesByPlaylist)),
        ("bluray: aligned enumeration still resolves by playlist evidence", Sync(AlignedEnumerationResolvesByEvidence)),
        ("bluray: playlist shared by editions is disambiguated by runtime", Sync(SharedPlaylistDisambiguatedByRuntime)),
        ("bluray: playlist shared with inseparable runtimes is refused", Sync(SharedPlaylistInseparableIsRefused)),
        ("bluray: playlist matched with conflicting runtime is refused", Sync(PlaylistWithConflictingRuntimeIsRefused)),
        ("bluray: missing playlist is refused when editions carry playlist identity", Sync(MissingPlaylistIsRefused)),
        ("bluray: unique runtime resolves when no playlist identity exists", Sync(UniqueRuntimeResolvesWithoutPlaylist)),
        ("bluray: duplicated runtime without playlist identity is refused", Sync(DuplicatedRuntimeIsRefused)),
        ("bluray: missing runtime match is refused", Sync(MissingRuntimeIsRefused)),
        ("bluray: empty edition list is refused", Sync(EmptyEditionListIsRefused)),
        ("bluray: resolution is identical for every input order", Sync(ResolutionIsOrderIndependent))
    ];

    // The Road, observed 2026-11-08: MakeMKV reported 38 titles; mpv/libbluray reported
    // 118 editions, and the main feature 00004.mpls (1:51:20, 16 chapters) was
    // libbluray edition 91 while MakeMKV index 1 sent bd://1 to an unrelated title.
    private static readonly BluRayTitle RoadMainFeature = new(
        Index: 1,
        IsActive: false,
        Duration: TimeSpan.Parse("1:51:20"),
        ChapterCount: 16,
        Playlist: "00004.mpls",
        Name: "The Road",
        IsLikelyMainFeature: true);

    private static readonly BluRayEditionInfo[] RoadEditions =
    [
        new(0, TimeSpan.Parse("0:00:30"), "00800.mpls", "title: 1 (00:00:30.000) (00800.mpls)"),
        new(1, TimeSpan.Parse("0:02:00"), "00010.mpls", "title: 2 (00:02:00.000) (00010.mpls)"),
        new(2, TimeSpan.Parse("0:13:47"), "00650.mpls", "title: 3 (00:13:47.000) (00650.mpls)"),
        new(90, TimeSpan.Parse("0:01:12"), "00700.mpls", "title: 91 (00:01:12.000) (00700.mpls)"),
        new(91, TimeSpan.Parse("1:51:20.716"), "00004.mpls", "title: 92 (01:51:20.716) (00004.mpls)"),
        new(92, TimeSpan.Parse("0:13:47"), "00650.mpls", "title: 93 (00:13:47.200) (00650.mpls)")
    ];

    // Blade Runner 2049 layout: 26 MakeMKV titles and 26 libbluray editions whose
    // indices happen to align; resolution must still come from playlist evidence.
    private static readonly BluRayTitle AlignedFeature = new(
        Index: 5,
        IsActive: false,
        Duration: TimeSpan.Parse("2:43:30"),
        ChapterCount: 16,
        Playlist: "00800.mpls",
        Name: "Blade Runner 2049",
        IsLikelyMainFeature: true);

    private static readonly BluRayEditionInfo[] AlignedEditions =
    [
        new(4, TimeSpan.Parse("0:28:00"), "00102.mpls", "title: 5 (00:28:00.000) (00102.mpls)"),
        new(5, TimeSpan.Parse("2:43:30.500"), "00800.mpls", "title: 6 (02:43:30.500) (00800.mpls)"),
        new(6, TimeSpan.Parse("0:10:00"), "00310.mpls", "title: 7 (00:10:00.000) (00310.mpls)")
    ];

    private static void EditionTitleParsing()
    {
        var (blurayDuration, blurayPlaylist) =
            BluRayEditionResolver.ParseEditionTitle("title: 92 (01:51:20.716) (00004.mpls)");
        Check(blurayDuration == TimeSpan.Parse("1:51:20.716"),
            $"Blu-ray edition runtime must parse, got {blurayDuration}");
        Check(string.Equals(blurayPlaylist, "00004.mpls", StringComparison.OrdinalIgnoreCase),
            $"Blu-ray edition playlist must parse, got {blurayPlaylist ?? "<null>"}");

        var (dvdDuration, dvdPlaylist) = BluRayEditionResolver.ParseEditionTitle("title: 1 (01:07:32.900)");
        Check(dvdDuration == TimeSpan.Parse("1:07:32.900"),
            $"DVD-style edition runtime must still parse, got {dvdDuration}");
        Check(dvdPlaylist is null, $"DVD-style edition has no playlist identity, got {dvdPlaylist ?? "<null>"}");

        var (menuDuration, menuPlaylist) = BluRayEditionResolver.ParseEditionTitle("Disc Menu");
        Check(menuDuration == TimeSpan.Zero && menuPlaylist is null,
            "Disc Menu must yield no runtime and no playlist");

        var (bareDuration, barePlaylist) = BluRayEditionResolver.ParseEditionTitle("title: 3");
        Check(bareDuration == TimeSpan.Zero && barePlaylist is null,
            "A bare edition label must yield no runtime and no playlist");

        var (nullDuration, nullPlaylist) = BluRayEditionResolver.ParseEditionTitle(null);
        Check(nullDuration == TimeSpan.Zero && nullPlaylist is null,
            "A null edition label must yield no runtime and no playlist");
    }

    private static void DivergentEnumerationResolvesByPlaylist()
    {
        var resolution = BluRayEditionResolver.Resolve(RoadMainFeature, RoadEditions);
        Check(resolution.IsResolved, $"The Road main feature must resolve: {resolution.Detail}");
        Check(resolution.Selector == 91,
            $"The resolver must select libbluray edition 91, got {resolution.Selector}");
        Check(resolution.Selector != RoadMainFeature.Index,
            "The raw MakeMKV index must never be used as the libbluray selector");
        Check(resolution.Detail.Contains("00004.mpls", StringComparison.OrdinalIgnoreCase),
            $"The resolution must name the playlist evidence, got: {resolution.Detail}");
    }

    private static void AlignedEnumerationResolvesByEvidence()
    {
        var resolution = BluRayEditionResolver.Resolve(AlignedFeature, AlignedEditions);
        Check(resolution.IsResolved, $"An aligned layout must still resolve: {resolution.Detail}");
        Check(resolution.Selector == 5, $"The resolver must select edition 5, got {resolution.Selector}");
        Check(resolution.Detail.Contains("00800.mpls", StringComparison.OrdinalIgnoreCase),
            $"Resolution must cite playlist evidence, not ordinal coincidence: {resolution.Detail}");

        // Same layout without playlist identity anywhere: the unique runtime must carry it.
        var withoutPlaylist = AlignedEditions
            .Select(edition => edition with { Playlist = null })
            .ToArray();
        var byRuntime = BluRayEditionResolver.Resolve(AlignedFeature, withoutPlaylist);
        Check(byRuntime.IsResolved && byRuntime.Selector == 5,
            $"A unique runtime must resolve without playlist identity: {byRuntime.Detail}");
        Check(byRuntime.Detail.Contains("runtime", StringComparison.OrdinalIgnoreCase),
            $"Runtime-based resolution must say so: {byRuntime.Detail}");
    }

    private static void SharedPlaylistDisambiguatedByRuntime()
    {
        // One playlist referenced by two editions (for example repeated index.bdmv
        // entries); only one is near the MakeMKV runtime.
        var editions = new[]
        {
            new BluRayEditionInfo(20, TimeSpan.Parse("0:00:45"), "00004.mpls", "title: 21 (00:00:45.000) (00004.mpls)"),
            new BluRayEditionInfo(91, TimeSpan.Parse("1:51:20.716"), "00004.mpls", "title: 92 (01:51:20.716) (00004.mpls)")
        };
        var resolution = BluRayEditionResolver.Resolve(RoadMainFeature, editions);
        Check(resolution.IsResolved && resolution.Selector == 91,
            $"Runtime must disambiguate a shared playlist: {resolution.Detail}");
    }

    private static void SharedPlaylistInseparableIsRefused()
    {
        var editions = new[]
        {
            new BluRayEditionInfo(20, TimeSpan.Parse("1:51:19.500"), "00004.mpls", "title: 21 (01:51:19.500) (00004.mpls)"),
            new BluRayEditionInfo(91, TimeSpan.Parse("1:51:20.716"), "00004.mpls", "title: 92 (01:51:20.716) (00004.mpls)")
        };
        var resolution = BluRayEditionResolver.Resolve(RoadMainFeature, editions);
        Check(!resolution.IsResolved, "A shared playlist with inseparable runtimes must be refused");
        Check(resolution.Detail.Contains("disambiguate", StringComparison.OrdinalIgnoreCase),
            $"The refusal must explain the ambiguity: {resolution.Detail}");
    }

    private static void PlaylistWithConflictingRuntimeIsRefused()
    {
        // The only playlist carrier disagrees with MakeMKV on runtime by minutes:
        // conflicting evidence, not a match.
        var editions = new[]
        {
            new BluRayEditionInfo(3, TimeSpan.Parse("0:44:00"), "00004.mpls", "title: 4 (00:44:00.000) (00004.mpls)")
        };
        var resolution = BluRayEditionResolver.Resolve(RoadMainFeature, editions);
        Check(!resolution.IsResolved, "A playlist match with a conflicting runtime must be refused");
        Check(resolution.Detail.Contains("conflicts", StringComparison.OrdinalIgnoreCase),
            $"The refusal must describe the conflict: {resolution.Detail}");
    }

    private static void MissingPlaylistIsRefused()
    {
        var requested = RoadMainFeature with { Playlist = "00999.mpls" };
        var resolution = BluRayEditionResolver.Resolve(requested, RoadEditions);
        Check(!resolution.IsResolved, "A playlist no edition carries must be refused");
        Check(resolution.Detail.Contains("00999.mpls", StringComparison.OrdinalIgnoreCase),
            $"The refusal must name the missing playlist: {resolution.Detail}");
    }

    private static void UniqueRuntimeResolvesWithoutPlaylist()
    {
        var requested = new BluRayTitle(7, false, TimeSpan.Parse("0:13:47"), ChapterCount: 2);
        var resolution = BluRayEditionResolver.Resolve(requested, RoadEditions);
        Check(!resolution.IsResolved,
            "Two editions at 0:13:47 make the runtime non-unique and must refuse: " + resolution.Detail);

        var uniqueRequested = new BluRayTitle(0, false, TimeSpan.Parse("0:02:00"), ChapterCount: 1);
        var uniqueResolution = BluRayEditionResolver.Resolve(uniqueRequested, RoadEditions);
        Check(uniqueResolution.IsResolved && uniqueResolution.Selector == 1,
            $"A unique runtime must resolve without playlist identity: {uniqueResolution.Detail}");
    }

    private static void DuplicatedRuntimeIsRefused()
    {
        var requested = new BluRayTitle(2, false, TimeSpan.Parse("0:13:47"), ChapterCount: 2);
        var resolution = BluRayEditionResolver.Resolve(requested, RoadEditions);
        Check(!resolution.IsResolved, "A duplicated runtime must be refused");
        Check(resolution.Detail.Contains("edition 2", StringComparison.OrdinalIgnoreCase) &&
              resolution.Detail.Contains("edition 92", StringComparison.OrdinalIgnoreCase),
            $"The refusal must name the competing editions: {resolution.Detail}");
    }

    private static void MissingRuntimeIsRefused()
    {
        var requested = new BluRayTitle(4, false, TimeSpan.Parse("3:00:00"), ChapterCount: 40);
        var resolution = BluRayEditionResolver.Resolve(requested, RoadEditions);
        Check(!resolution.IsResolved, "A runtime nothing is near must be refused");
        Check(resolution.Detail.Contains("3:00:00", StringComparison.OrdinalIgnoreCase),
            $"The refusal must name the unmatched runtime: {resolution.Detail}");
    }

    private static void EmptyEditionListIsRefused()
    {
        var resolution = BluRayEditionResolver.Resolve(RoadMainFeature, []);
        Check(!resolution.IsResolved, "An empty edition list must be refused");
        Check(resolution.Detail.Contains("no playable editions", StringComparison.OrdinalIgnoreCase),
            $"The refusal must explain that no editions were reported: {resolution.Detail}");
    }

    private static void ResolutionIsOrderIndependent()
    {
        var forward = BluRayEditionResolver.Resolve(RoadMainFeature, RoadEditions);
        var reversed = BluRayEditionResolver.Resolve(RoadMainFeature, RoadEditions.Reverse().ToArray());
        Check(forward.IsResolved && reversed.IsResolved && forward.Selector == reversed.Selector,
            $"Resolution must not depend on edition order: {forward.Selector} vs {reversed.Selector}");

        var requested = new BluRayTitle(4, false, TimeSpan.Parse("3:00:00"), ChapterCount: 40);
        var forwardRefusal = BluRayEditionResolver.Resolve(requested, RoadEditions);
        var reversedRefusal = BluRayEditionResolver.Resolve(requested, RoadEditions.Reverse().ToArray());
        Check(!forwardRefusal.IsResolved && !reversedRefusal.IsResolved,
            "Refusal must not depend on edition order");
    }
}
