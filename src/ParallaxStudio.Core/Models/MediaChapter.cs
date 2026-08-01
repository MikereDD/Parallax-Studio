namespace ParallaxStudio.Core.Models;

public sealed record MediaChapter(int Index, string Title, TimeSpan Start, bool IsActive = false)
{
    public string DisplayText => $"{Start:hh\\:mm\\:ss}  {Title}{(IsActive ? "  [Now]" : string.Empty)}";
}
