using ParallaxStudio.Core.Models;

namespace ParallaxStudio.Core.Events;

public sealed class PlaybackStateChangedEventArgs(PlaybackSnapshot snapshot) : EventArgs
{
    public PlaybackSnapshot Snapshot { get; } = snapshot;
}
