using ParallaxStudio.Core.Models;

namespace ParallaxStudio.Infrastructure.Playback;

internal sealed record DvdHostRequest(
    string Command,
    double? Number = null,
    int? Id = null,
    string? Kind = null,
    string? Path = null);

internal sealed record DvdHostResponse(
    bool Ok,
    string? Error = null,
    PlaybackSnapshot? Snapshot = null,
    MediaTrack[]? Tracks = null,
    MediaChapter[]? Chapters = null);
