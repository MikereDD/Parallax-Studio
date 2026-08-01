namespace ParallaxStudio.Core.Models;

public sealed record MediaChapter(int Index, string Title, TimeSpan Start, bool IsActive = false)
{
    public string TimeText => Start.TotalHours >= 1 ? $"{Start:hh\\:mm\\:ss}" : $"{Start:mm\\:ss}";
    public string PrimaryText => string.IsNullOrWhiteSpace(Title) ? $"Chapter {Index + 1}" : Title;
    public string StateText => IsActive ? "NOW PLAYING" : string.Empty;
    public string DisplayText => $"{TimeText}  {PrimaryText}{(IsActive ? "  [Now]" : string.Empty)}";
}
