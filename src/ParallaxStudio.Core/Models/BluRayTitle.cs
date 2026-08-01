namespace ParallaxStudio.Core.Models;

public sealed record BluRayTitle(
    int Index,
    bool IsActive,
    TimeSpan Duration,
    int ChapterCount = 0,
    string? Playlist = null,
    string? Name = null,
    bool IsLikelyMainFeature = false,
    bool IsLikelyDuplicate = false)
{
    public int DisplayNumber => Index + 1;
    public string PrimaryText => string.IsNullOrWhiteSpace(Name) ? $"Title {DisplayNumber}" : Name!;
    public string DurationText => Duration > TimeSpan.Zero
        ? Duration.TotalHours >= 1 ? Duration.ToString(@"h\:mm\:ss") : Duration.ToString(@"m\:ss")
        : "Duration unavailable";
    public string DetailText
    {
        get
        {
            var details = new List<string> { DurationText };
            if (ChapterCount > 0) details.Add($"{ChapterCount} chapter{(ChapterCount == 1 ? string.Empty : "s")}");
            if (!string.IsNullOrWhiteSpace(Playlist)) details.Add(Playlist!);
            return string.Join("  •  ", details);
        }
    }
    public string ClassificationText => IsLikelyMainFeature ? "LIKELY MAIN FEATURE"
        : IsLikelyDuplicate ? "POSSIBLE DUPLICATE"
        : Duration >= TimeSpan.FromMinutes(40) ? "FEATURE-LENGTH"
        : Duration >= TimeSpan.FromMinutes(3) ? "EXTRA" : "SHORT";
}
