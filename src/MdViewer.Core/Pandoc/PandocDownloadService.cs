using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MdViewer.Core.Pandoc;

public sealed record PandocDownloadResult(string Version, string PandocPath, long DownloadedBytes);

/// <summary>Downloads the latest official Windows x64 Pandoc release into md-viewer's own data folder.</summary>
public sealed partial class PandocDownloadService
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/jgm/pandoc/releases/latest";
    private static readonly HttpClient Http = CreateHttpClient();

    [GeneratedRegex(@"^pandoc-(?<version>.+)-windows-x86_64\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsZip();

    public async Task<PandocDownloadResult> DownloadLatestAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report("Finding the latest Pandoc release...");
        var asset = await GetLatestAssetAsync(cancellationToken);
        var workDirectory = Path.Combine(Path.GetTempPath(), $"md-viewer-pandoc-{Guid.NewGuid():N}");
        var zipPath = Path.Combine(workDirectory, asset.FileName);
        try
        {
            Directory.CreateDirectory(workDirectory);
            progress?.Report($"Downloading Pandoc {asset.Version}...");
            var bytes = await DownloadAsync(asset.Url, zipPath, progress, cancellationToken);

            if (asset.Sha256 is { } expected)
            {
                await using var stream = File.OpenRead(zipPath);
                var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The Pandoc download did not match GitHub's published SHA-256 digest.");
            }

            progress?.Report("Installing Pandoc...");
            Directory.CreateDirectory(PandocLocator.ToolDirectory);
            var staged = PandocLocator.FetchedPath + ".new";
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals("pandoc.exe", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException("The Pandoc archive does not contain pandoc.exe.");
                entry.ExtractToFile(staged, overwrite: true);
            }
            File.Move(staged, PandocLocator.FetchedPath, overwrite: true);
            return new PandocDownloadResult(asset.Version, PandocLocator.FetchedPath, bytes);
        }
        finally
        {
            try { if (Directory.Exists(workDirectory)) Directory.Delete(workDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<(string FileName, string Version, string Url, string? Sha256)> GetLatestAssetAsync(CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(LatestReleaseApiUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        foreach (var asset in json.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var match = WindowsZip().Match(name);
            if (!match.Success || asset.GetProperty("browser_download_url").GetString() is not { Length: > 0 } url) continue;
            if (!url.StartsWith("https://github.com/jgm/pandoc/releases/download/", StringComparison.OrdinalIgnoreCase)) continue;
            var digest = asset.TryGetProperty("digest", out var d) && d.GetString() is { } value && value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? value[7..] : null;
            return (name, match.Groups["version"].Value, url, digest);
        }
        throw new InvalidOperationException("The latest Pandoc release has no Windows x64 ZIP.");
    }

    private static async Task<long> DownloadAsync(string url, string path, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 17, useAsync: true);
        var buffer = new byte[1 << 17];
        long total = 0;
        var nextReport = 5;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
            if (length is > 0 && total * 100 / length.Value >= nextReport)
            {
                var percent = (int)(total * 100 / length.Value);
                progress?.Report($"Downloading Pandoc {percent}%...");
                nextReport = percent + 5;
            }
        }
        return total;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("md-viewer/2.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
