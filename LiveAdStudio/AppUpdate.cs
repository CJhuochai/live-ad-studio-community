using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiveAdStudio;

sealed record UpdateOffer(Version Version, Uri Download, long Size, string Sha256);

static class AppUpdate
{
    const string Repository = "CJhuochai/live-ad-studio-community";
    const long MaxPackageBytes = 256L * 1024 * 1024;
    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 1024 * 1024 };
    public static Version CurrentVersion => typeof(AppUpdate).Assembly.GetName().Version ?? new Version(0, 0, 0);

    static AppUpdate() => Client.DefaultRequestHeaders.UserAgent.ParseAdd("LiveAdStudio/" + CurrentVersion.ToString(3));

    public static async Task<UpdateOffer?> CheckAsync()
    {
        using var response = await Client.GetAsync($"https://raw.githubusercontent.com/{Repository}/main/update.json");
        response.EnsureSuccessStatusCode();
        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return ReadOffer(data.RootElement, CurrentVersion);
    }

    internal static UpdateOffer? ReadOffer(JsonElement manifest, Version installed)
    {
        var value = manifest.GetProperty("version").GetString();
        if (!Version.TryParse(value, out var version) || version.Build < 0 ||
            version.Revision >= 0 || value != version.ToString(3)) return null;
        if (new Version(version.Major, version.Minor, version.Build, 0) <= installed) return null;

        var size = manifest.GetProperty("size").GetInt64();
        var digest = manifest.GetProperty("sha256").GetString();
        if (size is <= 0 or > MaxPackageBytes || digest is null ||
            digest.Length != 64 || !digest.All(Uri.IsHexDigit)) return null;
        var tag = "v" + version.ToString(3);
        var name = $"LiveAdStudio-{tag}-Win11-x64.zip";
        var url = new Uri($"https://github.com/{Repository}/releases/download/{tag}/{name}");
        return new UpdateOffer(version, url, size, digest);
    }

    public static async Task PrepareAsync(UpdateOffer offer, string destination, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var ct = timeout.Token;
        using var response = await Client.GetAsync(offer.Download, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != offer.Size)
            throw new InvalidDataException("更新包大小与发布信息不一致。");
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await StageAsync(source, offer, destination, ct);
    }

    internal static async Task StageAsync(Stream source, UpdateOffer offer, string destination, CancellationToken ct)
    {
        var archivePath = destination + ".zip";
        try
        {
            await using (var archiveFile = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) != 0)
                {
                    received += read;
                    if (received > offer.Size) throw new InvalidDataException("更新包大小与发布信息不一致。");
                    hash.AppendData(buffer, 0, read);
                    await archiveFile.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                if (received != offer.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(offer.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("更新包校验失败，请稍后重试。");
            }
            using var archive = ZipFile.OpenRead(archivePath);
            var executables = archive.Entries.Where(e => e.Name.Equals("LiveAdStudio.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (executables.Length != 1 || executables[0].Length is <= 0 or > 350L * 1024 * 1024)
                throw new InvalidDataException("更新包缺少有效的程序文件。");
            await using (var executable = executables[0].Open())
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long written = 0;
                int read;
                while ((read = await executable.ReadAsync(buffer, ct)) != 0)
                {
                    written += read;
                    if (written > executables[0].Length) throw new InvalidDataException("更新包内的程序大小无效。");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                if (written != executables[0].Length) throw new InvalidDataException("更新包内的程序不完整。");
            }
            var info = FileVersionInfo.GetVersionInfo(destination);
            var executableVersion = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
            if (executableVersion != offer.Version)
                throw new InvalidDataException($"更新包内的程序版本 {executableVersion} 与发布版本 {offer.Version} 不一致。");
        }
        finally { File.Delete(archivePath); }
    }

    public static void InstallAfterExit(string executable, string staged)
    {
        var script = InstallerScript(executable, staged, Environment.ProcessId);
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(arg);
        using var helper = Process.Start(start) ?? throw new InvalidOperationException("无法启动更新程序。");
    }

    internal static string InstallerScript(string executable, string staged, int processId)
    {
        static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
        return $$"""
            $ErrorActionPreference = 'Stop'
            $exe = {{Literal(executable)}}
            $staged = {{Literal(staged)}}
            $backup = $exe + '.previous'
            $replaced = $false
            try {
                $parent = Get-Process -Id {{processId}} -ErrorAction SilentlyContinue
                if ($parent -and -not $parent.WaitForExit(60000)) { throw '等待旧程序退出超时。' }
                [IO.File]::Replace($staged, $exe, $backup)
                $replaced = $true
                Start-Process -FilePath $exe -WorkingDirectory ([IO.Path]::GetDirectoryName($exe))
            } catch {
                if ($replaced) {
                    try { [IO.File]::Replace($backup, $exe, $staged) } catch {}
                }
                Add-Type -AssemblyName System.Windows.Forms
                [void][Windows.Forms.MessageBox]::Show('更新失败。请重新打开工作台。若无法打开，可用程序旁的 .previous 文件恢复。','直播投流工作台')
            } finally {
                if ([IO.File]::Exists($staged)) { [IO.File]::Delete($staged) }
            }
            """;
    }
}
