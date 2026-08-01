using ParallaxStudio.Core.Enums;

namespace ParallaxStudio.Core.Models;

public sealed record PlaybackSnapshot(
    PlaybackState State,
    string? Source,
    string Title,
    TimeSpan Position,
    TimeSpan Duration,
    double Volume,
    bool IsMuted)
{
    public static PlaybackSnapshot Disconnected(double volume = 68) => new(
        PlaybackState.Disconnected,
        null,
        "Nothing playing",
        TimeSpan.Zero,
        TimeSpan.Zero,
        volume,
        false);
}
