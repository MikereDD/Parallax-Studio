using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ParallaxStudio.Core.Enums;
using ParallaxStudio.Core.Models;

namespace ParallaxStudio.Infrastructure.Playback;

internal enum OpticalIdentityClassification
{
    /// <summary>Full structural evidence was readable; the identity is drive-letter independent.</summary>
    Strong,
    /// <summary>Some trustworthy evidence remains; the identity stays scoped to the drive.</summary>
    Degraded,
    /// <summary>No distinguishing evidence remains; the identity must never reuse caches.</summary>
    Insufficient
}

internal sealed record OpticalDiscIdentityInfo(
    string Key,
    OpticalIdentityClassification Classification,
    string Summary)
{
    public bool IsReusable => Classification != OpticalIdentityClassification.Insufficient;
}

/// <summary>
/// Builds the session-scoped cache identity for an optical disc. A strong identity is
/// derived from the disc itself: kind, normalized volume label, and SHA-256 hashed
/// structural evidence from VIDEO_TS (DVD) or BDMV (Blu-ray). The same physical disc
/// therefore keeps its identity across drive letters, and two discs that merely share a
/// volume label cannot collide. Large stream files (VOB, .m2ts) are never read.
/// When the structural files cannot be read, the identity degrades to a drive-scoped key
/// over every remaining piece of trustworthy evidence (partial structural fingerprints,
/// reported volume size). When no distinguishing evidence remains, the identity is
/// classified insufficient so the caller can prevent stale cache reuse entirely.
/// </summary>
internal static class OpticalDiscIdentity
{
    // Identity files are small by format; refuse pathological sizes rather than hash them.
    private const long MaximumHashedFileBytes = 64 * 1024 * 1024;

    public static OpticalDiscIdentityInfo Build(DiscSource disc)
    {
        var context = Gather(disc);
        if (context.Structural is { IsStrong: true })
            return StrongResult(context);

        return FallbackResult(context, QueryVolumeSize(context.Root));
    }

    // Test seam: the volume size is injected so degraded-identity fixtures do not depend
    // on the host drive the temporary directory happens to live on.
    internal static OpticalDiscIdentityInfo Build(DiscSource disc, long? volumeSize)
    {
        var context = Gather(disc);
        if (context.Structural is { IsStrong: true })
            return StrongResult(context);

        return FallbackResult(context, volumeSize);
    }

    private static IdentityContext Gather(DiscSource disc)
    {
        ArgumentNullException.ThrowIfNull(disc);
        // Structure is read from DriveRoot itself (a drive root in production; a fixture
        // directory in tests). Fallback scoping uses the containing drive.
        var root = disc.DriveRoot;
        var structural = disc.Kind switch
        {
            DiscKind.Dvd => TryBuildDvdStructuralEvidence(root),
            DiscKind.BluRay => TryBuildBluRayStructuralEvidence(root),
            _ => null
        };

        return new IdentityContext(
            root,
            NormalizeDriveRoot(Path.GetPathRoot(root) ?? root),
            NormalizeLabel(disc.DisplayName),
            disc.Kind.ToString().ToUpperInvariant(),
            string.IsNullOrWhiteSpace(disc.DisplayName) ? "no label" : disc.DisplayName.Trim(),
            structural);
    }

    private static OpticalDiscIdentityInfo StrongResult(IdentityContext context)
    {
        var fingerprint = Fingerprint(context.Kind, context.Label, context.Structural!.Evidence);
        return new OpticalDiscIdentityInfo(
            $"OPTICAL|{context.Kind}|{context.Label}|{fingerprint}",
            OpticalIdentityClassification.Strong,
            $"{context.Kind} '{context.DisplayLabel}' strong identity {Prefix(fingerprint)} ({context.Structural.Description})");
    }

    private static OpticalDiscIdentityInfo FallbackResult(IdentityContext context, long? volumeSize)
    {
        var evidence = new List<string>();
        var descriptions = new List<string>();
        if (volumeSize is long size)
        {
            evidence.Add($"size={size.ToString(CultureInfo.InvariantCulture)}");
            descriptions.Add("volume size");
        }

        if (context.Structural is not null)
        {
            evidence.Add(context.Structural.Evidence);
            descriptions.Add(context.Structural.Description);
        }

        if (evidence.Count == 0)
        {
            return new OpticalDiscIdentityInfo(
                $"OPTICAL|{context.Kind}|{context.Label}|INSUFFICIENT|{context.Drive}",
                OpticalIdentityClassification.Insufficient,
                $"{context.Kind} '{context.DisplayLabel}' at {context.Drive} has no readable structural or size evidence");
        }

        var fingerprint = Fingerprint(context.Kind, context.Label, string.Join(";", evidence));
        return new OpticalDiscIdentityInfo(
            $"OPTICAL|{context.Kind}|{context.Label}|DEGRADED|{context.Drive}|{fingerprint}",
            OpticalIdentityClassification.Degraded,
            $"{context.Kind} '{context.DisplayLabel}' degraded identity {Prefix(fingerprint)} at {context.Drive} ({string.Join(", ", descriptions)})");
    }

    private static StructuralEvidence? TryBuildDvdStructuralEvidence(string root)
    {
        try
        {
            var videoTs = Path.Combine(root, "VIDEO_TS");
            var mainIfo = Path.Combine(videoTs, "VIDEO_TS.IFO");
            if (!File.Exists(mainIfo))
                return TryBuildPartialIfoEvidence(videoTs);

            var complete = true;
            var parts = new List<string>();
            foreach (var ifo in EnumerateSorted(videoTs, "*.IFO"))
            {
                var evidence = TryFileEvidence(ifo);
                if (evidence is null)
                {
                    complete = false;
                    continue;
                }

                parts.Add(evidence);
            }

            var description = $"{parts.Count} IFO file{(parts.Count == 1 ? string.Empty : "s")} hashed";
            return new StructuralEvidence($"ifo=[{string.Join(",", parts)}]", complete, description);
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            return null;
        }
    }

    private static StructuralEvidence? TryBuildPartialIfoEvidence(string videoTs)
    {
        try
        {
            if (!Directory.Exists(videoTs))
                return null;

            var parts = EnumerateSorted(videoTs, "*.IFO")
                .Select(TryFileEvidence)
                .Where(evidence => evidence is not null)
                .ToArray();
            return parts.Length == 0
                ? null
                : new StructuralEvidence(
                    $"partial-ifo=[{string.Join(",", parts)}]",
                    IsStrong: false,
                    $"{parts.Length} readable IFO file{(parts.Length == 1 ? string.Empty : "s")}, VIDEO_TS.IFO unreadable");
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            return null;
        }
    }

    private static StructuralEvidence? TryBuildBluRayStructuralEvidence(string root)
    {
        try
        {
            var bdmv = Path.Combine(root, "BDMV");
            var indexPath = Path.Combine(bdmv, "index.bdmv");
            var indexEvidence = File.Exists(indexPath) ? TryFileEvidence(indexPath) : null;
            var movieObjectPath = Path.Combine(bdmv, "MovieObject.bdmv");
            var movieObjectEvidence = File.Exists(movieObjectPath) ? TryFileEvidence(movieObjectPath) : null;
            var playlistDirectory = Path.Combine(bdmv, "PLAYLIST");
            var playlists = Directory.Exists(playlistDirectory) ? TryPlaylistEvidence(playlistDirectory) : null;

            if (indexEvidence is null)
            {
                var partial = new List<string>();
                if (movieObjectEvidence is not null) partial.Add($"mo={movieObjectEvidence}");
                if (playlists is not null) partial.Add($"mpls=[{playlists}]");
                return partial.Count == 0
                    ? null
                    : new StructuralEvidence(
                        string.Join(";", partial),
                        IsStrong: false,
                        "index.bdmv unreadable, partial BDMV evidence");
            }

            var complete = true;
            var parts = new List<string> { $"index={indexEvidence}" };
            var descriptions = new List<string> { "index.bdmv hashed" };
            if (File.Exists(movieObjectPath))
            {
                if (movieObjectEvidence is not null)
                {
                    parts.Add($"mo={movieObjectEvidence}");
                    descriptions.Add("MovieObject.bdmv hashed");
                }
                else
                {
                    complete = false;
                }
            }

            if (Directory.Exists(playlistDirectory))
            {
                if (playlists is not null)
                {
                    parts.Add($"mpls=[{playlists}]");
                    descriptions.Add($"{playlists.Split(',', StringSplitOptions.RemoveEmptyEntries).Length} playlists fingerprinted");
                }
                else
                {
                    complete = false;
                }
            }

            return new StructuralEvidence(
                string.Join(";", parts),
                complete,
                string.Join(", ", descriptions));
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            return null;
        }
    }

    private static string? TryPlaylistEvidence(string playlistDirectory)
    {
        try
        {
            var parts = EnumerateSorted(playlistDirectory, "*.mpls")
                .Select(path =>
                {
                    try
                    {
                        var info = new FileInfo(path);
                        return $"{info.Name.ToUpperInvariant()}:{info.Length.ToString(CultureInfo.InvariantCulture)}";
                    }
                    catch (Exception ex) when (IsIoFailure(ex))
                    {
                        return null;
                    }
                })
                .ToArray();
            return parts.Any(part => part is null) ? null : string.Join(",", parts);
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            return null;
        }
    }

    private static string? TryFileEvidence(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaximumHashedFileBytes)
                return null;

            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            return string.Join(':',
                info.Name.ToUpperInvariant(),
                info.Length.ToString(CultureInfo.InvariantCulture),
                info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                hash);
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            return null;
        }
    }

    private static string[] EnumerateSorted(string directory, string pattern) =>
        Directory.EnumerateFiles(directory, pattern)
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToArray();

    private static long? QueryVolumeSize(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.TotalSize : null;
        }
        catch (Exception ex) when (IsIoFailure(ex) || ex is ArgumentException)
        {
            return null;
        }
    }

    private static string Fingerprint(string kind, string label, string evidence)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}\n{label}\n{evidence}"));
        return Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
    }

    private static string Prefix(string fingerprint) => fingerprint[..8];

    private static string NormalizeLabel(string? label) =>
        string.IsNullOrWhiteSpace(label)
            ? "-"
            : string.Join(' ', label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private static string NormalizeDriveRoot(string root) =>
        root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();

    private static bool IsIoFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException;

    private sealed record IdentityContext(
        string Root,
        string Drive,
        string Label,
        string Kind,
        string DisplayLabel,
        StructuralEvidence? Structural);

    private sealed record StructuralEvidence(string Evidence, bool IsStrong, string Description);
}
