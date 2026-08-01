namespace ParallaxStudio.App.Interop;

public sealed class VideoHandleCreatedEventArgs(nint handle) : EventArgs
{
    public nint Handle { get; } = handle;
}
