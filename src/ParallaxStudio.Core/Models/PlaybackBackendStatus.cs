namespace ParallaxStudio.Core.Models;

public sealed record PlaybackBackendStatus(
    bool IsAvailable,
    string Description,
    string? LibraryPath = null,
    string? Error = null);
