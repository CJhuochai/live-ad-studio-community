using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace LiveAdStudio;

static class UpdateChecks
{
    public static void Run()
    {
        var version = new Version(3, 0, 0);
        const string name = "LiveAdStudio-v3.0.0-Win11-x64.zip";
        const string url = "https://github.com/CJhuochai/live-ad-studio-community/releases/download/v3.0.0/" + name;
        using var package = new MemoryStream();
        var sample = Path.Combine(Environment.SystemDirectory, "where.exe");
        using (var zip = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
            zip.CreateEntryFromFile(sample, "release/LiveAdStudio.exe");
        var digest = Convert.ToHexString(SHA256.HashData(package.ToArray()));
        JsonElement Manifest(string published = "3.0.0", string? hash = null, long? size = null)
            => JsonSerializer.SerializeToElement(new
            {
                version = published, size = size ?? package.Length, sha256 = hash ?? digest,
                download = "https://example.invalid/update.zip"
            });
        var installed = new Version(2, 2, 0, 0);
        var offer = AppUpdate.ReadOffer(Manifest(), installed);
        if (offer?.Version != version || offer.Download.AbsoluteUri != url ||
            AppUpdate.ReadOffer(Manifest(), new Version(3, 0, 0, 0)) != null ||
            AppUpdate.ReadOffer(Manifest(published: "3.0.0-beta"), installed) != null ||
            AppUpdate.ReadOffer(Manifest(size: 0), installed) != null ||
            AppUpdate.ReadOffer(Manifest(hash: "invalid"), installed) != null)
            throw new InvalidDataException("Update offer verification failed.");

        var staged = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".update");
        var sampleCopy = staged + ".sample";
        try
        {
            // A system executable can report compatibility-adjusted versions at its original path.
            File.Copy(sample, sampleCopy);
            var file = FileVersionInfo.GetVersionInfo(sampleCopy);
            var sampleVersion = new Version(file.FileMajorPart, file.FileMinorPart, file.FileBuildPart);
            package.Position = 0;
            AppUpdate.StageAsync(package, offer with { Version = sampleVersion }, staged, CancellationToken.None).GetAwaiter().GetResult();
            if (!File.ReadAllBytes(staged).SequenceEqual(File.ReadAllBytes(sample)))
                throw new InvalidDataException("Staged update differs from the verified package.");
            File.Delete(staged);
            package.Position = 0;
            try
            {
                AppUpdate.StageAsync(package, offer with { Sha256 = new string('0', 64) }, staged, CancellationToken.None).GetAwaiter().GetResult();
                throw new InvalidOperationException("Update with an invalid digest was accepted.");
            }
            catch (InvalidDataException) when (!File.Exists(staged) && !File.Exists(staged + ".zip")) { }
        }
        finally { File.Delete(staged); File.Delete(sampleCopy); }
    }
}
