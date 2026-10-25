namespace ParallaxStudio.Core.Models;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 3;
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

    // Cinema behavior
    public bool CinemaAutoExpand { get; set; } = true;
    public bool RestoreSidebarWhenPlaybackStops { get; set; } = true;
    public bool ShowSidebarWhenPaused { get; set; }

    // Fullscreen behavior
    public bool FullscreenHideTransport { get; set; } = true;
    public bool FullscreenShowControlsOnMouseMove { get; set; } = true;
    public int FullscreenControlsAutoHideSeconds { get; set; } = 3;
    public bool FullscreenCursorAutoHide { get; set; } = true;
    public bool EscapeExitsFullscreen { get; set; } = true;

    // Window behavior
    public bool RememberWindowGeometry { get; set; } = true;
}
