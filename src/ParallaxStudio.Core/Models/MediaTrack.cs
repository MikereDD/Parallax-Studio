using ParallaxStudio.Core.Enums;

namespace ParallaxStudio.Core.Models;

public sealed record MediaTrack(
    int Id,
    MediaTrackKind Kind,
    string Label,
    string? Language,
    string? Codec,
    string? Details,
    bool IsDefault,
    bool IsForced,
    bool IsSelected)
{
    public string DisplayText
    {
        get
        {
            var flags = new List<string>();
            if (IsDefault) flags.Add("Default");
            if (IsForced) flags.Add("Forced");
            if (IsSelected) flags.Add("Active");
            var metadata = string.Join(" • ", new[] { Language, Codec, Details }.Where(value => !string.IsNullOrWhiteSpace(value)));
            var suffix = flags.Count == 0 ? string.Empty : $"  [{string.Join(", ", flags)}]";
            return string.IsNullOrWhiteSpace(metadata) ? $"{Label}{suffix}" : $"{Label} — {metadata}{suffix}";
        }
    }
}
