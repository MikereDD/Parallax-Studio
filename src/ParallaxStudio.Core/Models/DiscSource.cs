using ParallaxStudio.Core.Enums;

namespace ParallaxStudio.Core.Models;

public sealed record DiscSource(
    string DriveRoot,
    string DisplayName,
    DiscKind Kind,
    bool IsReady)
{
    public string KindLabel => Kind switch
    {
        DiscKind.Dvd => "DVD",
        DiscKind.BluRay => "Blu-ray",
        DiscKind.Data => "Data disc",
        _ => "Unknown"
    };
}
