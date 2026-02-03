using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using Aurora.Core.IO;
using Aurora.Core.Logging;
using Aurora.Core.Models;
using Aurora.Core.Parsing;
using Aurora.Core.Security;

namespace Aurora.Core.Net;

public class RepoManager
{
    private readonly string _rootPath;
    private readonly HttpClient _client;
    public bool SkipSignatureCheck { get; set; } = false;

    public RepoManager(string rootPath)
    {
        _rootPath = rootPath;

        // Optimized Handler: Forces IPv4 and handles decompression automatically.
        // Forces IPv4 because many repo mirrors have broken IPv6 routing which causes 30s hangs.
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = async (context, cancellationToken) =>
            {
                var entry = await Dns.GetHostEntryAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, cancellationToken);
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(new IPEndPoint(entry.AddressList[0], context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, true);
            }
        };

        _client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        _client.DefaultRequestHeaders.Clear();
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Aurora Package Manager; Pacman v2)");
    }

    public async Task SyncRepositoriesAsync(Action<string, string> onProgress)
    {
        var configPath = PathHelper.GetPath(_rootPath, "etc/aurora/repolist");
        if (!File.Exists(configPath))
        {
            AuLogger.Error("No repositories configured at etc/aurora/repolist");
            return;
        }

        var repos = RepoConfigParser.Parse(File.ReadAllText(configPath));
        if (repos.Count == 0) return;

        var dbDir = PathHelper.GetPath(_rootPath, "var/lib/aurora");
        Directory.CreateDirectory(dbDir);
        var gpgHome = PathHelper.GetPath(_rootPath, "etc/aurora/gnupg");

        foreach (var repo in repos.Values.Where(r => r.Enabled))
        {
            // Use JSON extension for the repo database
            var repoFileName = $"{repo.Id}.json";
            var targetFile = Path.Combine(dbDir, repoFileName);
            var sigFile = targetFile + ".sig";

            onProgress(repo.Name, "Syncing...");

            try
            {
                // 1. Download Database
                await FetchFile(repo.Url, repoFileName, targetFile);

                // 2. Verification
                if (!SkipSignatureCheck)
                {
                    await FetchFile(repo.Url, repoFileName + ".sig", sigFile);

                    if (!GpgHelper.VerifySignature(targetFile, sigFile, Directory.Exists(gpgHome) ? gpgHome : null))
                    {
                        File.Delete(targetFile);
                        File.Delete(sigFile);
                        throw new Exception("Invalid GPG Signature! Database discarded.");
                    }
                }
                
                onProgress(repo.Name, "Done");
            }
            catch (Exception ex)
            {
                AuLogger.Error($"Failed to sync '{repo.Name}': {ex.Message}");
                onProgress(repo.Name, $"Failed: {ex.Message}");
            }
        }
    }

    public async Task<string?> DownloadPackageAsync(Package pkg, string cacheDir, Action<long?, long> onProgress)
    {
        // CRITICAL: Use pkg.FileName from the database if available.
        // This avoids filename mismatches caused by version epochs (e.g. 1:1.0).
        var filename = !string.IsNullOrEmpty(pkg.FileName) 
            ? pkg.FileName 
            : $"{pkg.Name}-{pkg.Version}-{pkg.Arch}.au";

        // Sanitize filename for local storage just in case the server sent something weird
        var localFilename = filename.Replace(":", "_");
        var cachePath = Path.Combine(cacheDir, localFilename);

        // Check cache and integrity
        if (File.Exists(cachePath))
        {
            if (string.IsNullOrEmpty(pkg.Checksum) || HashHelper.ComputeFileHash(cachePath) == pkg.Checksum)
            {
                var info = new FileInfo(cachePath);
                onProgress(info.Length, info.Length);
                return cachePath;
            }
            AuLogger.Debug($"Cache mismatch for {localFilename}, re-downloading...");
            File.Delete(cachePath);
        }
        
        Directory.CreateDirectory(cacheDir);

        var configPath = PathHelper.GetPath(_rootPath, "etc/aurora/repolist");
        if (!File.Exists(configPath)) throw new Exception("No repolist found.");
        
        var repos = RepoConfigParser.Parse(File.ReadAllText(configPath));
        
        foreach (var repo in repos.Values.Where(r => r.Enabled))
        {
            try
            {
                // We use the raw filename from the repo for the URL construction
                await FetchFile(repo.Url, filename, cachePath, onProgress);

                // Verify SHA256 integrity after download
                if (!string.IsNullOrEmpty(pkg.Checksum))
                {
                    var actualSum = HashHelper.ComputeFileHash(cachePath);
                    if (!string.Equals(actualSum, pkg.Checksum, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(cachePath);
                        throw new InvalidDataException($"Integrity check failed: SHA256 mismatch.");
                    }
                }
                
                return cachePath;
            }
            catch (Exception ex)
            {
                AuLogger.Debug($"Mirror {repo.Name} failed for {filename}: {ex.Message}");
                // Fallthrough to next repo
            }
        }

        return null;
    }

    private async Task FetchFile(string baseUrl, string filename, string destination, Action<long?, long>? onProgress = null)
    {
        // Ensure the base URL ends with a slash for Uri combining
        var baseUriString = baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/";
        var baseUri = new Uri(baseUriString);
        
        // Combine carefully. Uri constructor handles escaping characters in filename.
        var fullUri = new Uri(baseUri, filename);

        if (fullUri.Scheme == "file")
        {
            var sourcePath = fullUri.LocalPath;
            if (!File.Exists(sourcePath)) throw new FileNotFoundException($"Local file mirror not found: {sourcePath}");
            File.Copy(sourcePath, destination, overwrite: true);
            var info = new FileInfo(sourcePath);
            onProgress?.Invoke(info.Length, info.Length);
        }
        else
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, fullUri) { Version = HttpVersion.Version11 };
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            
            await using var downloadStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[32768]; // 32KB buffer for faster writing
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