using System.Windows;
using System.Windows.Media;

namespace ParallaxStudio.App.Services;

public static class StudioAppearanceService
{
    public static IReadOnlyList<string> Themes { get; } =
    [
        "Dark Monochrome", "OLED Black", "Graphite", "Midnight Indigo", "Obsidian Gold", "Aurora Pulse"
    ];

    public static IReadOnlyList<string> TypographyProfiles { get; } = ["Cadence", "Precision", "Clear"];
    public static IReadOnlyList<string> TextSizes { get; } = ["Standard", "Large", "Extra Large"];

    public static void ApplyTheme(string? name)
    {
        var file = name switch
        {
            "OLED Black" => "OLEDBlack.xaml",
            "Graphite" => "Graphite.xaml",
            "Midnight Indigo" => "MidnightIndigo.xaml",
            "Obsidian Gold" => "ObsidianGold.xaml",
            "Aurora Pulse" => "AuroraPulse.xaml",
            _ => "DarkMonochrome.xaml"
        };

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var current = dictionaries.FirstOrDefault(d =>
        {
            var source = d.Source?.OriginalString;
            return source is not null
                && source.StartsWith("Themes/", StringComparison.Ordinal)
                && source != "Themes/Typography.xaml"
                && source != "Themes/Controls.xaml";
        });
        var replacement = new ResourceDictionary { Source = new Uri($"Themes/{file}", UriKind.Relative) };
        if (current is null) dictionaries.Insert(0, replacement);
        else dictionaries[dictionaries.IndexOf(current)] = replacement;
    }

    public static void ApplyTypography(string? profile, string? textSize)
    {
        var (body, display) = profile switch
        {
            "Precision" => ("Bahnschrift", "Bahnschrift SemiBold"),
            "Clear" => ("Segoe UI", "Segoe UI Semibold"),
            _ => ("Segoe UI Variable Text", "Segoe UI Variable Display")
        };
        Application.Current.Resources["StudioFontFamily"] = new FontFamily(body);
        Application.Current.Resources["StudioDisplayFontFamily"] = new FontFamily(display);
        Application.Current.Resources["StudioTextScale"] = textSize switch
        {
            "Large" => 1.12,
            "Extra Large" => 1.25,
            _ => 1.0
        };
    }
}
