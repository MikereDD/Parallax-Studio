using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ParallaxStudio.App.Services;

/// <summary>Loads a small, detached artwork image from the media's own directory.</summary>
public sealed class LocalArtworkService
{
    private const long MaximumFileBytes = 32 * 1024 * 1024;
    private const int MaximumDecodeDimension = 480;
    private const int MaximumSourceDimension = 32768;
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png"];
    private static readonly string[] FolderNames = ["poster", "folder", "cover"];

    public Task<ImageSource?> LoadAsync(string? source, CancellationToken cancellationToken = default) =>
        Task.Run(() => Load(source, cancellationToken), cancellationToken);

    private static ImageSource? Load(string? source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = GetCandidates(source);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // A stream with OnLoad avoids WPF's URI cache and releases the artwork
                // file immediately, so users can replace/delete covers during playback.
                using var stream = new FileStream(candidate, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length is <= 0 or > MaximumFileBytes) continue;

                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                if (decoder.Frames.Count == 0) continue;
                var frame = decoder.Frames[0];
                var width = frame.PixelWidth;
                var height = frame.PixelHeight;
                if (width <= 0 || height <= 0 || width > MaximumSourceDimension || height > MaximumSourceDimension ||
                    (long)width * height > 100_000_000) continue;

                cancellationToken.ThrowIfCancellationRequested();
                stream.Position = 0;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                // Bound the longest side without stretching portrait or landscape art.
                if (width >= height) image.DecodePixelWidth = Math.Min(width, MaximumDecodeDimension);
                else image.DecodePixelHeight = Math.Min(height, MaximumDecodeDimension);
                image.EndInit();
                image.Freeze();
                cancellationToken.ThrowIfCancellationRequested();
                return image;
            }
            catch (Exception ex) when (IsArtworkFailure(ex))
            {
                // Artwork is optional. A damaged or locked candidate must not prevent
                // the next local candidate from being tried or affect video playback.
            }
        }

        return null;
    }

    private static IReadOnlyList<string> GetCandidates(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return [];
        try
        {
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
            {
                if (!uri.IsFile) return [];
                source = uri.LocalPath;
            }

            // Do not interpret mpv URLs, relative names, or arbitrary working-directory
            // paths as media folders. UNC media shares remain valid filesystem paths.
            if (!Path.IsPathFullyQualified(source)) return [];
            source = Path.GetFullPath(source);
            var isDirectory = Directory.Exists(source);
            var directory = isDirectory ? source : Path.GetDirectoryName(source);
            if (string.IsNullOrEmpty(directory)) return [];

            var candidates = new List<string>();
            if (!isDirectory)
            {
                var basename = Path.GetFileNameWithoutExtension(source);
                if (!string.IsNullOrEmpty(basename))
                    candidates.AddRange(Extensions.Select(extension => Path.Combine(directory, basename + extension)));
            }

            foreach (var name in FolderNames)
                candidates.AddRange(Extensions.Select(extension => Path.Combine(directory, name + extension)));
            return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex) when (IsArtworkFailure(ex))
        {
            return [];
        }
    }

    private static bool IsArtworkFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or SecurityException or FormatException or COMException or InvalidOperationException;
}
