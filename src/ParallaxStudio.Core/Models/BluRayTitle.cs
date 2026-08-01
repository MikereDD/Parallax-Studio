namespace ParallaxStudio.Core.Models;

public sealed record BluRayTitle(int Index, bool IsActive)
{
    public int DisplayNumber => Index + 1;
    public string DisplayText => IsActive
        ? $"Title {DisplayNumber}  •  currently playing"
        : $"Title {DisplayNumber}";
}
