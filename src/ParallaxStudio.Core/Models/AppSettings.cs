namespace ParallaxStudio.Core.Models;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 2;
    public double Volume { get; set; } = 68;
    public string? LastOpenDirectory { get; set; }
    public double WindowWidth { get; set; } = 1380;
    public double WindowHeight { get; set; } = 860;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool IsMaximized { get; set; }
    public string ThemeName { get; set; } = "Dark Monochrome";
    public string TypographyProfile { get; set; } = "Cadence";
    public string TextSize { get; set; } = "Standard";
    public bool ReduceMotion { get; set; }
}
