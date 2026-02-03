using Aurora.Core.Contract;
using Aurora.Core.IO;
using Aurora.Core.Logging;
using Aurora.Core.Models;
using Aurora.Core.Parsing;
using Aurora.Core.Security;
using System.Net;

namespace Aurora.Core.Net;

public class RepoManager
{
    private readonly string _rootPath;
    private readonly HttpClient _client;
    public bool SkipSignatureCheck { get; set; } = false;

    public RepoManager(string rootPath)
    {
        _rootPath = rootPath;

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) };
        _client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Aurora Package Manager)");
    }

    /// <summary>
    /// Reads /etc/aurora/repolist and downloads/verifies the .aurepo files.
    /// </summary>
    public async Task SyncRepositoriesAsync(Action<string, string> onProgress)
    {
        var configPath = PathHelper.GetPath(_rootPath, "etc/aurora/repolist");
        if (!File.Exists(configPath))
        {
            AuLogger.Info("No repositories configured at etc/aurora/repolist");
            return;
        }

        var repos = RepoConfigParser.Parse(File.ReadAllText(configPath));
        if (repos.Count == 0) return;

        var dbDir = PathHelper.GetPath(_rootPath, "var/lib/aurora");
        Directory.CreateDirectory(dbDir);
        var gpgHome = PathHelper.GetPath(_rootPath, "etc/aurora/gnupg");

        foreach (var repo in repos.Values.Where(r => r.Enabled))
        {
            var repoFileName = $"{repo.Id}.aurepo";
            var targetFile = Path.Combine(dbDir, repoFileName);
            var sigFile = targetFile + ".asc";

            onProgress(repo.Name, "Syncing...");

            try
            {
                await FetchFile(repo.Url, repoFileName, targetFile);
                await FetchFile(repo.Url, repoFileName + ".asc", sigFile);

                if (!SkipSignatureCheck)
                {
                    onProgress(repo.Name, "Verifying...");
                    if (!GpgHelper.VerifySignature(targetFile, sigFile, Directory.Exists(gpgHome) ? gpgHome : null))
                    {
                        File.Delete(targetFile);
                        File.Delete(sigFile);
                        throw new Exception("Invalid GPG Signature! Repository is untrusted.");
                    }
                }
                
                onProgress(repo.Name, SkipSignatureCheck ? "Done (Unverified)" : "Done (Signed)");
            }
            catch (Exception ex)
            {
                AuLogger.Error($"Failed to sync '{repo.Name}': {ex.Message}");
                onProgress(repo.Name, $"Failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Downloads a specific package (.au) file from the configured repositories.
    /// </summary>
    public async Task<string?> DownloadPackageAsync(Package pkg, string cacheDir, Action<long?, long> onProgress)
    {
        var filename = $"{pkg.Name}-{pkg.Version}-{pkg.Arch}.au";
        var cachePath = Path.Combine(cacheDir, filename);

        // 1. Check Cache with Integrity
        if (File.Exists(cachePath))
        {
            if (!string.IsNullOrEmpty(pkg.Checksum))
            {
                var cachedHash = HashHelper.ComputeFileHash(cachePath);
                if (cachedHash == pkg.Checksum)
                {
                    var info = new FileInfo(cachePath);
                    onProgress(info.Length, info.Length); // Signal 100%
                    return cachePath;
                }
                File.Delete(cachePath); // Corrupted, re-download
            }
            else
            {
                return cachePath; // No checksum, trust it for now
            }
        }

        Directory.CreateDirectory(cacheDir);

        // 2. Try all configured repos
        var repos = RepoConfigParser.Parse(File.ReadAllText(PathHelper.GetPath(_rootPath, "etc/aurora/repolist")));
        
        foreach (var repo in repos.Values.Where(r => r.Enabled))
        {
            try
            {
                // Attempt to fetch the file from this repository
                await FetchFile(repo.Url, filename, cachePath, onProgress);

                // 3. Verify Download
                if (!string.IsNullOrEmpty(pkg.Checksum))
                {
                    var downloadedHash = HashHelper.ComputeFileHash(cachePath);
                    if (downloadedHash != pkg.Checksum)
                    {
                        File.Delete(cachePath);
                        throw new InvalidDataException($"Security Error: Checksum mismatch for {pkg.Name}.");
                    }
                }
                
                // Success!
                return cachePath;
            }
            catch
            {
                // File not found in this repo, or download failed. Try the next one.
            }
        }

        return null; // Not found in any repo
    }

    /// <summary>
    /// Internal helper for fetching any file (repo metadata or package).
    /// </summary>
    private async Task FetchFile(string baseUrl, string filename, string destination, Action<long?, long>? onProgress = null)
    {
        var baseUri = new Uri(baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
        var fullUri = new Uri(baseUri, filename);

        if (fullUri.Scheme == "file")
        {
            var sourcePath = fullUri.LocalPath;
            if (!File.Exists(sourcePath)) throw new FileNotFoundException($"Remote file not found: {sourcePath}");
            File.Copy(sourcePath, destination, overwrite: true);
            
            // Report 100% for local files
            var info = new FileInfo(sourcePath);
            onProgress?.Invoke(info.Length, info.Length);
        }
        else
        {
            var request = new HttpRequestMessage(HttpMethod.Get, fullUri);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            
            await using var downloadStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[16384];
            long totalDownloaded = 0;
            int bytesRead;

            while ((bytesRead = await downloadStream.ReadAsync(buffer)) != 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalDownloaded += bytesRead;
                onProgress?.Invoke(totalBytes, totalDownloaded);
            }
        }
    }
}