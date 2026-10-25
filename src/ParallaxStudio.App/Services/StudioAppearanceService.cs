using System.Windows;
using System.Windows.Media;
using ParallaxStudio.App.Common;

namespace ParallaxStudio.App.Services;

public static class StudioAppearanceService
{
    public static StudioMotionPreferences Motion { get; } = new();

    static StudioAppearanceService()
    {
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(SystemParameters.ClientAreaAnimation) or null or "")) return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) Motion.RefreshSystemPreference();
            else dispatcher.BeginInvoke(new Action(Motion.RefreshSystemPreference));
        };
    }

    public static void ApplyMotion(bool reduceMotion) => Motion.SetPreference(reduceMotion);
    public static IReadOnlyList<string> Themes { get; } =
    [
        "Dark Monochrome", "OLED Black", "Graphite", "Midnight Indigo", "Obsidian Gold", "Aurora Pulse"
    ];

    public static IReadOnlyList<string> TypographyProfiles { get; } = ["Cadence", "Precision", "Clear"];
    public static IReadOnlyList<string> TextSizes { get; } = ["Standard", "Large", "Extra Large"];

    public static IReadOnlyList<string> InterfaceDensities { get; } = ["Comfortable", "Compact"];

    public static void ApplyDensity(string? density)
    {
        var compact = density == "Compact";
        var resources = Application.Current.Resources;
        resources["StudioSidebarWidth"] = compact ? 304d : 330d;
        resources["StudioSidebarCardPadding"] = new Thickness(compact ? 16 : 20);
        resources["StudioSidebarItemPadding"] = compact ? new Thickness(12, 9, 12, 9) : new Thickness(14, 12, 14, 12);
        resources["StudioTransportPadding"] = compact ? new Thickness(16, 10, 16, 10) : new Thickness(20, 16, 20, 16);
        resources["StudioIconButtonSize"] = compact ? 36d : 42d;
        resources["StudioPlayButtonWidth"] = compact ? 54d : 62d;
        resources["StudioPlayButtonHeight"] = compact ? 42d : 48d;
        resources["StudioTransportStatusMargin"] = new Thickness(0, compact ? 10 : 14, 0, 0);
        resources["StudioFloatingTransportPadding"] = compact ? new Thickness(12, 9, 12, 9) : new Thickness(16, 12, 16, 12);
    }
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
                && (source.StartsWith("Themes/", StringComparison.Ordinal)
                    || source.StartsWith("pack://application:,,,/ParallaxStudio.App;component/Themes/", StringComparison.Ordinal))
                && !source.EndsWith("Themes/Typography.xaml", StringComparison.Ordinal)
                && !source.EndsWith("Themes/Controls.xaml", StringComparison.Ordinal);
        });
        var replacement = new ResourceDictionary { Source = new Uri($"pack://application:,,,/ParallaxStudio.App;component/Themes/{file}", UriKind.Absolute) };
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

// Shared by templates regardless of their item DataContext, including floating controls.
public sealed class StudioMotionPreferences : ObservableObject
{
    private bool _requestedReduceMotion;
    private bool _reduceMotion = !SystemParameters.ClientAreaAnimation;

    public bool ReduceMotion => _reduceMotion;

    internal void SetPreference(bool reduceMotion)
    {
        _requestedReduceMotion = reduceMotion;
        RefreshSystemPreference();
    }

    internal void RefreshSystemPreference() =>
        SetProperty(ref _reduceMotion, _requestedReduceMotion || !SystemParameters.ClientAreaAnimation, nameof(ReduceMotion));
}
