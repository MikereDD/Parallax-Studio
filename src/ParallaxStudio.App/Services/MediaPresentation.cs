using System.IO;
using System.Text.RegularExpressions;
using ParallaxStudio.Core.Enums;
using ParallaxStudio.Core.Models;

namespace ParallaxStudio.App.Services;

public sealed record MediaDetailRow(string Label, string Value);

// Presentation only: never infer a disc title mapping, HDR format, or audio profile.
public static class MediaPresentation
{
    public static string SourceKind(string? source) => source switch
    {
        { } s when s.StartsWith("Blu-ray disc", StringComparison.OrdinalIgnoreCase) => "Blu-ray",
        { } s when s.StartsWith("DVD disc", StringComparison.OrdinalIgnoreCase) => "DVD",
        { Length: > 0 } => "Local file",
        _ => string.Empty
    };

    public static string SourceSummary(string? source)
    {
        var kind = SourceKind(source);
        if (kind.Length == 0) return "Open a video to begin";
        if (kind != "Local file")
            return string.Join(" · ", new[] { kind }.Concat(source!.Split('•').Skip(1).Select(p => p.Trim())));

        try
        {
            var extension = Path.GetExtension(source)?.TrimStart('.').ToUpperInvariant();
            var folder = Path.GetFileName(Path.GetDirectoryName(source));
            return string.Join(" · ", new[] { extension, folder }.Where(p => !string.IsNullOrWhiteSpace(p))) is { Length: > 0 } summary
                ? summary : kind;
        }
        catch (ArgumentException) { return kind; }
    }

    public static IReadOnlyList<string> Badges(string? source, IReadOnlyList<MediaTrack> tracks)
    {
        var badges = new List<string>();
        var kind = SourceKind(source);
        if (kind.Length > 0) badges.Add(kind);
        var video = Selected(tracks, MediaTrackKind.Video);
        if (video?.Details is { } details)
        {
            var resolution = Regex.Match(details, @"\b\d{2,5}[×x]\d{2,5}\b");
            if (resolution.Success) badges.Add(resolution.Value);
            if (details.Contains("HDR (PQ)", StringComparison.OrdinalIgnoreCase)) badges.Add("HDR · PQ");
            else if (details.Contains("HDR (HLG)", StringComparison.OrdinalIgnoreCase)) badges.Add("HDR · HLG");
        }
        var audio = Selected(tracks, MediaTrackKind.Audio);
        if (!string.IsNullOrWhiteSpace(audio?.Codec)) badges.Add(AudioCodec(audio.Codec));
        return badges;
    }

    public static IReadOnlyList<MediaDetailRow> Details(PlaybackSnapshot snapshot,
        IReadOnlyList<MediaTrack> tracks, IReadOnlyList<MediaChapter> chapters)
    {
        var rows = new List<MediaDetailRow>();
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) rows.Add(new(label, value));
        }
        if (!string.IsNullOrWhiteSpace(snapshot.Source)) Add("Source", SourceSummary(snapshot.Source));
        if (snapshot.Duration > TimeSpan.Zero) Add("Runtime", Time(snapshot.Duration));
        var video = Selected(tracks, MediaTrackKind.Video);
        if (video is not null) Add("Video", Join(VideoCodec(video.Codec), video.Details));
        var audio = Selected(tracks, MediaTrackKind.Audio);
        if (audio is not null) Add("Audio", Join(audio.PrimaryText, audio.SecondaryText));
        if (tracks.Count > 0)
        {
            var subtitles = tracks.FirstOrDefault(t => t.Kind == MediaTrackKind.Subtitle && t.IsSelected);
            Add("Subtitles", subtitles?.PrimaryText ?? "Off");
        }
        if (chapters.Count > 0) Add("Chapters", chapters.Count.ToString());
        return rows;
    }

    public static string Time(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value:mm\\:ss}" : value.ToString(@"mm\:ss");

    private static MediaTrack? Selected(IReadOnlyList<MediaTrack> tracks, MediaTrackKind kind) =>
        tracks.FirstOrDefault(t => t.Kind == kind && t.IsSelected) ?? tracks.FirstOrDefault(t => t.Kind == kind);

    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static string? VideoCodec(string? codec) => codec?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "hevc" or "h265" or "hev1" or "hvc1" => "HEVC",
        "h264" or "avc1" => "H.264",
        "av1" => "AV1",
        "vp9" => "VP9",
        "mpeg2video" => "MPEG-2",
        "vc1" => "VC-1",
        _ => codec.ToUpperInvariant()
    };

    private static string AudioCodec(string codec) => codec.Trim().ToLowerInvariant() switch
    {
        "truehd" => "TrueHD",
        "eac3" or "e-ac-3" => "DD+",
        "ac3" or "ac-3" => "Dolby Digital",
        "dts" or "dca" => "DTS",
        "dts-hd" => "DTS-HD",
        "pcm-dvd" or "pcm-bluray" or "pcm_s16le" or "pcm_s24le" => "PCM",
        _ => codec.ToUpperInvariant()
    };
}
