using System.Globalization;
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
    public string PrimaryText
    {
        get
        {
            if (Id <= 0 && Kind == MediaTrackKind.Subtitle) return "Subtitles Off";

            var language = FormatLanguage(Language);
            var format = FormatCodec(Codec, Kind);
            var role = InferRole();
            return string.Join(" — ", new[] { language, format, role }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }
    }

    public string SecondaryText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Details)) parts.Add(FormatDetails(Details));
            if (IsDefault) parts.Add("Default");
            if (IsForced) parts.Add("Forced");
            return string.Join(" • ", parts);
        }
    }

    public string StateText => IsSelected ? "ACTIVE" : string.Empty;
    public string DisplayText => string.IsNullOrWhiteSpace(SecondaryText) ? PrimaryText : $"{PrimaryText}  •  {SecondaryText}";

    private string? InferRole()
    {
        var source = $"{Label} {Details}".ToLowerInvariant();
        if (source.Contains("commentary")) return "Commentary";
        if (source.Contains("descriptive") || source.Contains("description") || source.Contains("visually impaired")) return "Audio Description";
        if (source.Contains("hearing impaired") || source.Contains("sdh")) return "SDH";
        if (source.Contains("forced")) return "Forced";

        var generic = Kind switch
        {
            MediaTrackKind.Audio => $"audio {Id}",
            MediaTrackKind.Subtitle => $"subtitle {Id}",
            MediaTrackKind.Video => $"video {Id}",
            _ => string.Empty
        };
        return string.Equals(Label.Trim(), generic, StringComparison.OrdinalIgnoreCase) ? null : Label.Trim();
    }

    private static string FormatLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return "Unknown language";
        var normalized = language.Trim().ToLowerInvariant();
        return normalized switch
        {
            "eng" or "en" => "English",
            "spa" or "es" => "Spanish",
            "fra" or "fre" or "fr" => "French",
            "deu" or "ger" or "de" => "German",
            "ita" or "it" => "Italian",
            "jpn" or "ja" => "Japanese",
            "por" or "pt" => "Portuguese",
            "rus" or "ru" => "Russian",
            "zho" or "chi" or "zh" => "Chinese",
            "kor" or "ko" => "Korean",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(normalized)
        };
    }

    private static string FormatCodec(string? codec, MediaTrackKind kind)
    {
        if (string.IsNullOrWhiteSpace(codec)) return kind == MediaTrackKind.Subtitle ? "Subtitle" : "Unknown format";
        var normalized = codec.Trim().ToLowerInvariant().Replace('_', '-');
        return normalized switch
        {
            "dts" or "dca" => "DTS",
            "dts-hd" => "DTS-HD",
            "truehd" => "Dolby TrueHD",
            "eac3" or "e-ac-3" => "Dolby Digital Plus",
            "ac3" or "ac-3" => "Dolby Digital",
            "aac" => "AAC",
            "mp2" or "mp3" or "mpga" => "MPEG Audio",
            "flac" => "FLAC",
            "pcm-dvd" or "pcm-bluray" or "pcm-s16le" or "pcm-s24le" => "PCM",
            "hdmv-pgs-subtitle" => "PGS Subtitle",
            "dvd-subtitle" => "DVD Subtitle",
            "subrip" => "SRT Subtitle",
            "ass" or "ssa" => "ASS Subtitle",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(normalized.Replace('-', ' '))
        };
    }

    private static string FormatDetails(string details)
    {
        var normalized = details.Trim().ToLowerInvariant();
        return normalized switch
        {
            "stereo" or "2.0" => "Stereo",
            "mono" or "1.0" => "Mono",
            "5.1(side)" or "5.1" => "5.1",
            "7.1" => "7.1",
            _ => details.Trim()
        };
    }
}
