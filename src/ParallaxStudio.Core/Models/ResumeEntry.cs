namespace ParallaxStudio.Core.Models;

public sealed record ResumeEntry(
    string Path,
    double PositionSeconds,
    double DurationSeconds,
    DateTimeOffset UpdatedUtc);
