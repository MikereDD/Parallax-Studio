using ParallaxStudio.Core.Enums;
using ParallaxStudio.Core.Models;
using ParallaxStudio.Core.Services;

namespace ParallaxStudio.Infrastructure.Discs;

public sealed class OpticalDiscService(IAppLogger logger) : IDiscService
{
    public Task<IReadOnlyList<DiscSource>> GetAvailableDiscsAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<DiscSource>>(() => Scan(cancellationToken), cancellationToken);

    private IReadOnlyList<DiscSource> Scan(CancellationToken cancellationToken)
    {
        var results = new List<DiscSource>();

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.CDRom))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!drive.IsReady)
                {
                    results.Add(new DiscSource(drive.Name, $"{drive.Name} No media", DiscKind.Unknown, false));
                    continue;
                }

                var root = drive.RootDirectory.FullName;
                var kind = DetectKind(root);
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? kind.ToString() : drive.VolumeLabel;
                results.Add(new DiscSource(root, $"{drive.Name} {label}", kind, true));
            }
            catch (Exception ex)
            {
                _ = logger.WarningAsync($"Could not inspect optical drive {drive.Name}: {ex.Message}");
                results.Add(new DiscSource(drive.Name, $"{drive.Name} Unavailable", DiscKind.Unknown, false));
            }
        }

        return results;
    }

    private static DiscKind DetectKind(string root)
    {
        if (Directory.Exists(Path.Combine(root, "BDMV"))) return DiscKind.BluRay;
        if (Directory.Exists(Path.Combine(root, "VIDEO_TS"))) return DiscKind.Dvd;
        return DiscKind.Data;
    }
}
