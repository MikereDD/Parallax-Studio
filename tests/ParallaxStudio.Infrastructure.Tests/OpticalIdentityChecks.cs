using ParallaxStudio.Core.Enums;
using ParallaxStudio.Core.Models;
using ParallaxStudio.Infrastructure.Playback;

namespace ParallaxStudio.Infrastructure.Tests;

/// <summary>
/// Phase 6B: the optical cache identity must be derived from the disc itself (strong,
/// drive-letter independent), degraded to a drive-scoped key over every remaining piece
/// of trustworthy evidence, or classified insufficient so stale title/mapping caches are
/// never reused. Timestamps are secondary evidence, never sole uniqueness evidence.
/// </summary>
internal static partial class Program
{
    private static (string Name, Func<Task> Run)[] OpticalIdentityChecks =>
    [
        ("identity: same DVD structure on different roots shares one strong key", Sync(SameDvdAcrossRootsSharesStrongKey)),
        ("identity: same Blu-ray structure on different roots shares one strong key", Sync(SameBluRayAcrossRootsSharesStrongKey)),
        ("identity: same label with different IFO content produces different keys", Sync(SameLabelDifferentContentDiffers)),
        ("identity: changed structural fingerprint changes the key", Sync(StructuralChangeChangesKey)),
        ("identity: DVD and Blu-ray identities cannot collide", Sync(DvdAndBluRayDoNotCollide)),
        ("identity: timestamps are secondary evidence, never sole uniqueness", Sync(TimestampsAreSecondaryEvidence)),
        ("identity: degraded identity keeps readable evidence and stays drive-scoped", Sync(DegradedKeepsEvidenceDriveScoped)),
        ("identity: same-drive same-label media diverge when evidence exists", Sync(SameDriveSameLabelDivergeByEvidence)),
        ("identity: insufficient evidence is never reused as a cache key", InsufficientIdentityIsNeverReusedAsync),
        ("identity: key construction is deterministic and order-independent", Sync(DeterministicOrderIndependent))
    ];

    private static readonly DateTime IdentityStamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static void SameDvdAcrossRootsSharesStrongKey()
    {
        using var driveD = new DiscFixture("dvd-d", DiscKind.Dvd, "THE_ROAD", DvdFiles());
        using var driveE = new DiscFixture("dvd-e", DiscKind.Dvd, "THE_ROAD", DvdFiles());

        var identityD = OpticalDiscIdentity.Build(driveD.Disc, volumeSize: null);
        var identityE = OpticalDiscIdentity.Build(driveE.Disc, volumeSize: null);

        Check(identityD.Classification == OpticalIdentityClassification.Strong, "A readable DVD must produce a strong identity");
        Check(identityD.Key == identityE.Key, "The same physical DVD on another drive root must keep its strong key");
        Check(!identityD.Key.Contains(driveD.Root, StringComparison.OrdinalIgnoreCase) &&
              !identityD.Key.Contains(driveE.Root, StringComparison.OrdinalIgnoreCase),
            "A strong key must not embed the drive root");
    }

    private static void SameBluRayAcrossRootsSharesStrongKey()
    {
        var files = BluRayFiles(playlists: [("00000.mpls", "PL-ONE"), ("00001.mpls", "PL-TWO")]);
        using var driveD = new DiscFixture("bd-d", DiscKind.BluRay, "THEY_LIVE", files);
        using var driveE = new DiscFixture("bd-e", DiscKind.BluRay, "THEY_LIVE", files);

        var identityD = OpticalDiscIdentity.Build(driveD.Disc, volumeSize: null);
        var identityE = OpticalDiscIdentity.Build(driveE.Disc, volumeSize: null);

        Check(identityD.Classification == OpticalIdentityClassification.Strong, "A readable Blu-ray must produce a strong identity");
        Check(identityD.Key == identityE.Key, "The same physical Blu-ray on another drive root must keep its strong key");
        Check(identityD.Key.StartsWith("OPTICAL|BLURAY|", StringComparison.Ordinal), "The strong key must name the Blu-ray namespace");
    }

    private static void SameLabelDifferentContentDiffers()
    {
        // Identical label AND identical timestamps: only content may separate the keys,
        // proving timestamps are never the sole uniqueness evidence.
        using var first = new DiscFixture("diff-a", DiscKind.Dvd, "DR_WHO", DvdFiles(mainIfo: "VMG-CONTENT-A"));
        using var second = new DiscFixture("diff-b", DiscKind.Dvd, "DR_WHO", DvdFiles(mainIfo: "VMG-CONTENT-B"));

        Check(OpticalDiscIdentity.Build(first.Disc, null).Key != OpticalDiscIdentity.Build(second.Disc, null).Key,
            "Same volume label with different VIDEO_TS.IFO content must produce different keys");
    }

    private static void StructuralChangeChangesKey()
    {
        using var dvdBase = new DiscFixture("struct-a", DiscKind.Dvd, "SHOW_D1", DvdFiles());
        using var dvdExtra = new DiscFixture("struct-b", DiscKind.Dvd, "SHOW_D1",
            [.. DvdFiles(), (@"VIDEO_TS\VTS_03_0.IFO", "VTS3-CONTENT")]);
        Check(OpticalDiscIdentity.Build(dvdBase.Disc, null).Key != OpticalDiscIdentity.Build(dvdExtra.Disc, null).Key,
            "An added VTS IFO must change the DVD identity");

        using var bdBase = new DiscFixture("struct-c", DiscKind.BluRay, "MOVIE",
            BluRayFiles(playlists: [("00000.mpls", "PL-ONE")]));
        using var bdExtra = new DiscFixture("struct-d", DiscKind.BluRay, "MOVIE",
            BluRayFiles(playlists: [("00000.mpls", "PL-ONE"), ("00001.mpls", "PL-TWO")]));
        Check(OpticalDiscIdentity.Build(bdBase.Disc, null).Key != OpticalDiscIdentity.Build(bdExtra.Disc, null).Key,
            "A changed playlist fingerprint must change the Blu-ray identity");
    }

    private static void DvdAndBluRayDoNotCollide()
    {
        using var dvd = new DiscFixture("kind-dvd", DiscKind.Dvd, "SAME_LABEL", DvdFiles());
        using var bluRay = new DiscFixture("kind-bd", DiscKind.BluRay, "SAME_LABEL",
            BluRayFiles(playlists: [("00000.mpls", "PL-ONE")]));

        var dvdKey = OpticalDiscIdentity.Build(dvd.Disc, null).Key;
        var bluRayKey = OpticalDiscIdentity.Build(bluRay.Disc, null).Key;
        Check(dvdKey != bluRayKey, "DVD and Blu-ray identities must never collide");
        Check(dvdKey.StartsWith("OPTICAL|DVD|", StringComparison.Ordinal) &&
              bluRayKey.StartsWith("OPTICAL|BLURAY|", StringComparison.Ordinal),
            "The kind segment must separate the DVD and Blu-ray namespaces");
    }

    private static void TimestampsAreSecondaryEvidence()
    {
        var later = IdentityStamp.AddHours(1);
        using var stamped = new DiscFixture("ts-a", DiscKind.Dvd, "STAMPED", IdentityStamp, DvdFiles());
        using var restamped = new DiscFixture("ts-b", DiscKind.Dvd, "STAMPED", later, DvdFiles());
        Check(OpticalDiscIdentity.Build(stamped.Disc, null).Key != OpticalDiscIdentity.Build(restamped.Disc, null).Key,
            "A changed timestamp must change the key as secondary evidence");

        using var identical = new DiscFixture("ts-c", DiscKind.Dvd, "STAMPED", IdentityStamp, DvdFiles());
        Check(OpticalDiscIdentity.Build(stamped.Disc, null).Key == OpticalDiscIdentity.Build(identical.Disc, null).Key,
            "Identical content and metadata must produce identical keys");
    }

    private static void DegradedKeepsEvidenceDriveScoped()
    {
        // VIDEO_TS.IFO missing but a VTS IFO readable: degraded, drive-scoped, stable.
        using var partial = new DiscFixture("degraded", DiscKind.Dvd, "WEAK_DISC",
            (@"VIDEO_TS\VTS_01_0.IFO", "VTS1-CONTENT"));

        var first = OpticalDiscIdentity.Build(partial.Disc, volumeSize: 4_700_000_000);
        var second = OpticalDiscIdentity.Build(partial.Disc, volumeSize: 4_700_000_000);

        Check(first.Classification == OpticalIdentityClassification.Degraded, "Partial structural evidence must classify as degraded");
        Check(first.IsReusable, "A degraded identity with evidence remains reusable within the session");
        Check(first.Key == second.Key, "A degraded identity must be stable for the same media");
        Check(first.Key.Contains("DEGRADED", StringComparison.Ordinal), "The degraded key must say what it is");
        var normalizedDrive = (Path.GetPathRoot(partial.Root) ?? partial.Root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        Check(first.Key.Contains(normalizedDrive, StringComparison.Ordinal), "A degraded identity must stay scoped to its drive");
    }

    private static void SameDriveSameLabelDivergeByEvidence()
    {
        // Sequential same-label media in the same drive, simulated by rewriting the
        // readable evidence in place on one root.
        using var drive = new DiscFixture("sequential", DiscKind.Dvd, "DR_WHO",
            (@"VIDEO_TS\VTS_01_0.IFO", "DISC-A-VTS1"));

        var discA = OpticalDiscIdentity.Build(drive.Disc, volumeSize: 4_700_000_000);
        drive.Rewrite(@"VIDEO_TS\VTS_01_0.IFO", "DISC-B-VTS1-DIFFERENT", IdentityStamp);
        var discB = OpticalDiscIdentity.Build(drive.Disc, volumeSize: 4_700_000_000);

        Check(discA.Classification == OpticalIdentityClassification.Degraded &&
              discB.Classification == OpticalIdentityClassification.Degraded,
            "Both partial-evidence media must classify as degraded");
        Check(discA.Key != discB.Key,
            "Same-drive same-label media must not share an identity when distinguishing evidence exists");

        var resized = OpticalDiscIdentity.Build(drive.Disc, volumeSize: 8_500_000_000);
        Check(resized.Key != discB.Key, "A differing reported volume size must change the degraded identity");
    }

    private static async Task InsufficientIdentityIsNeverReusedAsync()
    {
        // No VIDEO_TS structure and no volume size: insufficient. The service must mint a
        // fresh, non-reusable key per establishment so stale caches can never be hit.
        using var empty = new DiscFixture("empty", DiscKind.Dvd, "DR_WHO");
        var built = OpticalDiscIdentity.Build(empty.Disc, volumeSize: null);
        Check(built.Classification == OpticalIdentityClassification.Insufficient, "No evidence must classify as insufficient");
        Check(!built.IsReusable, "An insufficient identity must not be reusable");

        var log = new TestLogger();
        await using var service = NewService(log);
        var firstKey = service.EstablishDiscIdentityForTesting(built);
        var secondKey = service.EstablishDiscIdentityForTesting(built);
        Check(firstKey != secondKey, "The service must mint a fresh key per establishment when evidence is insufficient");
        Check(log.Count("Optical identity insufficient") == 1, "The insufficient identity must be logged once per session");
        log.AssertNoErrors();
    }

    private static void DeterministicOrderIndependent()
    {
        var files = DvdFiles();
        using var forward = new DiscFixture("order-f", DiscKind.Dvd, "ORDER_TEST", files);
        using var reverse = new DiscFixture("order-r", DiscKind.Dvd, "ORDER_TEST", IdentityStamp, files, reverse: true);
        Check(OpticalDiscIdentity.Build(forward.Disc, null).Key == OpticalDiscIdentity.Build(reverse.Disc, null).Key,
            "File creation order must not affect the identity");

        var built = OpticalDiscIdentity.Build(forward.Disc, null);
        Check(OpticalDiscIdentity.Build(forward.Disc, null).Key == built.Key, "Identity construction must be deterministic");

        using var sloppyLabel = new DiscFixture("order-s", DiscKind.Dvd, "  order_test  ", files);
        Check(OpticalDiscIdentity.Build(sloppyLabel.Disc, null).Key == built.Key,
            "Volume labels must be normalized before keying");
    }

    private static (string RelativePath, string Content)[] DvdFiles(
        string mainIfo = "VMG-MAIN",
        string vts1 = "VTS1-CONTENT",
        string vts2 = "VTS2-CONTENT") =>
    [
        (@"VIDEO_TS\VIDEO_TS.IFO", mainIfo),
        (@"VIDEO_TS\VTS_01_0.IFO", vts1),
        (@"VIDEO_TS\VTS_02_0.IFO", vts2)
    ];

    private static (string RelativePath, string Content)[] BluRayFiles(
        string index = "INDEX-BDMV-CONTENT",
        string movieObject = "MOVIE-OBJECT-CONTENT",
        params (string Name, string Content)[] playlists) =>
        new[] { (@"BDMV\index.bdmv", index), (@"BDMV\MovieObject.bdmv", movieObject) }
            .Concat(playlists.Select(playlist => ($@"BDMV\PLAYLIST\{playlist.Name}", playlist.Content)))
            .ToArray();

    private sealed class DiscFixture : IDisposable
    {
        public DiscFixture(string name, DiscKind kind, string label, params (string RelativePath, string Content)[] files)
            : this(name, kind, label, IdentityStamp, files) { }

        public DiscFixture(
            string name,
            DiscKind kind,
            string label,
            DateTime stamp,
            (string RelativePath, string Content)[] files,
            bool reverse = false)
        {
            Root = Path.Combine(Path.GetTempPath(), $"parallax-identity-{name}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            foreach (var (relativePath, content) in reverse ? files.Reverse() : files)
            {
                var path = Path.Combine(Root, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
                File.SetLastWriteTimeUtc(path, stamp);
            }

            Disc = new DiscSource(Root, label, kind, true);
        }

        public string Root { get; }
        public DiscSource Disc { get; }

        public void Rewrite(string relativePath, string content, DateTime stamp)
        {
            var path = Path.Combine(Root, relativePath);
            File.WriteAllText(path, content);
            File.SetLastWriteTimeUtc(path, stamp);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch { }
        }
    }
}
