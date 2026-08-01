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
    public bool IsTechnicalItem => Duration > TimeSpan.Zero && Duration < TimeSpan.FromSeconds(75);
    public string PrimaryText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name) && !Name.Equals("The Road", StringComparison.OrdinalIgnoreCase)) return Name!;
            if (IsLikelyMainFeature) return "The Road — Main Feature";
            if (Duration >= TimeSpan.FromMinutes(10) && Duration < TimeSpan.FromMinutes(30)) return $"Bonus Feature — {DurationText}";
            if (Duration >= TimeSpan.FromMinutes(3) && Duration < TimeSpan.FromMinutes(10)) return $"Deleted Scene / Featurette Candidate — {DurationText}";
            if (Duration >= TimeSpan.FromMinutes(1) && Duration < TimeSpan.FromMinutes(3)) return $"Short Bonus Item — {DurationText}";
            if (IsTechnicalItem) return $"Disc Intro / Warning — {DurationText}";
            return $"Title {DisplayNumber}";
        }
    }
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
        : IsTechnicalItem ? "INTRO / WARNING"
        : IsLikelyDuplicate ? "POSSIBLE DUPLICATE"
        : Duration >= TimeSpan.FromMinutes(40) ? "FEATURE-LENGTH"
        : Duration >= TimeSpan.FromMinutes(10) ? "BONUS FEATURE"
        : Duration >= TimeSpan.FromMinutes(3) ? "DELETED SCENE / EXTRA"
        : "SHORT BONUS";
}
