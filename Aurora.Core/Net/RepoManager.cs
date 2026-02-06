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
        if (!File.Exists(configPath)) return;

        var repos = RepoConfigParser.Parse(File.ReadAllText(configPath));
        var dbDir = PathHelper.GetPath(_rootPath, "var/lib/aurora");
        Directory.CreateDirectory(dbDir);
        var gpgHome = PathHelper.GetPath(_rootPath, "etc/aurora/gnupg");

        foreach (var repo in repos.Values.Where(r => r.Enabled))
        {
            var repoFileName = $"{repo.Id}.json";
            var targetFile = Path.Combine(dbDir, repoFileName);
            var sigFile = targetFile + ".sig";

            onProgress(repo.Name, "Syncing...");

            try
            {
                await FetchFile(repo.Url, repoFileName, targetFile);

                if (!SkipSignatureCheck)
                {
                    await FetchFile(repo.Url, repoFileName + ".sig", sigFile);
                    if (!GpgHelper.VerifySignature(targetFile, sigFile, Directory.Exists(gpgHome) ? gpgHome : null))
                    {
                        File.Delete(targetFile);
                        File.Delete(sigFile);
                        throw new Exception("Invalid GPG Signature!");
                    }
                }
                onProgress(repo.Name, "Done");
            }
            catch (Exception ex)
            {
                onProgress(repo.Name, $"Failed: {ex.Message}");
            }
        }
    }

    public async Task<string?> DownloadPackageAsync(Package pkg, string cacheDir, Action<long?, long> onProgress)
    {
        // Use Filename from DB (which may contain ':')
        var filename = !string.IsNullOrEmpty(pkg.FileName) 
            ? pkg.FileName 
            : $"{pkg.Name}-{pkg.Version}-{pkg.Arch}.au";

        // LOCAL PATH FIX: Replace ':' with '_' because colons are illegal/problematic on 
        // many filesystems (NTFS, APFS) and cause issues with some Linux tools.
        var localSafeFilename = filename.Replace(":", "_");
        var cachePath = Path.Combine(cacheDir, localSafeFilename);

        if (File.Exists(cachePath))
        {
            if (string.IsNullOrEmpty(pkg.Checksum) || HashHelper.ComputeFileHash(cachePath) == pkg.Checksum)
            {
                var info = new FileInfo(cachePath);
                onProgress(info.Length, info.Length);
                return cachePath;
            }
            File.Delete(cachePath);
        }
        
        Directory.CreateDirectory(cacheDir);
        var configPath = PathHelper.GetPath(_rootPath, "etc/aurora/repolist");
        var repos = RepoConfigParser.Parse(File.ReadAllText(configPath));
        
        foreach (var repo in repos.Values.Where(r => r.Enabled))
        {
            try
            {
                // DOWNLOAD FIX: Pass the raw filename with colons to FetchFile
                await FetchFile(repo.Url, filename, cachePath, onProgress);

                if (!string.IsNullOrEmpty(pkg.Checksum))
                {
                    if (!string.Equals(HashHelper.ComputeFileHash(cachePath), pkg.Checksum, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(cachePath);
                        throw new InvalidDataException("Integrity mismatch.");
                    }
                }
                return cachePath;
            }
            catch { /* Try next mirror */ }
        }
        return null;
    }

    private async Task FetchFile(string baseUrl, string filename, string destination, Action<long?, long>? onProgress = null)
    {
        var baseUriString = baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/";
        var escapedFilename = filename.Replace(":", "%3A");
        var fullUri = new Uri(baseUriString + escapedFilename);

        // --- DEBUG LOGGING ---
        AuLogger.Debug($"[Network] Requesting: {fullUri}");
        // ---------------------

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
            
            if (!response.IsSuccessStatusCode)
            {
                // Log the failure details
                AuLogger.Error($"[Network] 404 Fail. URL: {fullUri} | Status: {response.StatusCode}");
                throw new Exception($"Mirror error: {(int)response.StatusCode} {response.ReasonPhrase} ({fullUri})");
            }

            var totalBytes = response.Content.Headers.ContentLength;
            await using var downloadStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[32768];
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