using Aurora.Core.Contract;
using Aurora.Core.IO;
using Aurora.Core.Logging;
using Aurora.Core.Models;
using Aurora.Core.Parsing;
using Aurora.Core.Security;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace Aurora.Core.Net;

public class RepoManager
{
    private readonly string _rootPath;
    private readonly HttpClient _client; // No longer static
    public bool SkipSignatureCheck { get; set; } = false;

    // The logic is now in the instance constructor
    public RepoManager(string rootPath)
    {
        _rootPath = rootPath;

        // This setup now only runs when a RepoManager is actually created,
        // not when the application starts.
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = async (context, cancellationToken) =>
            {
                // Force IPv4 addresses to prevent hangs on misconfigured mirrors
                var entry = await Dns.GetHostEntryAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, cancellationToken);
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(new IPEndPoint(entry.AddressList[0], context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, true);
            }
        };

        _client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        _client.DefaultRequestHeaders.Clear();
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Aurora Package Manager)");
    }

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

                if (!SkipSignatureCheck && !GpgHelper.VerifySignature(targetFile, sigFile, Directory.Exists(gpgHome) ? gpgHome : null))
                {
                    File.Delete(targetFile);
                    File.Delete(sigFile);
                    throw new Exception("Invalid GPG Signature!");
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
        var filename = $"{pkg.Name}-{pkg.Version}-{pkg.Arch}.au";
        var cachePath = Path.Combine(cacheDir, filename);

        if (File.Exists(cachePath) && !string.IsNullOrEmpty(pkg.Checksum) && HashHelper.ComputeFileHash(cachePath) == pkg.Checksum)
        {
            var info = new FileInfo(cachePath);
            onProgress(info.Length, info.Length);
            return cachePath;
        }
        
        if (File.Exists(cachePath)) File.Delete(cachePath);
        Directory.CreateDirectory(cacheDir);

        var repos = RepoConfigParser.Parse(File.ReadAllText(PathHelper.GetPath(_rootPath, "etc/aurora/repolist")));
        
        foreach (var repo in repos.Values.Where(r => r.Enabled))
        {
            try
            {
                await FetchFile(repo.Url, filename, cachePath, onProgress);

                if (!string.IsNullOrEmpty(pkg.Checksum) && HashHelper.ComputeFileHash(cachePath) != pkg.Checksum)
                {
                    File.Delete(cachePath);
                    throw new InvalidDataException($"Checksum mismatch for {pkg.Name}.");
                }
                
                return cachePath;
            }
            catch { /* Try next repo */ }
        }

        return null;
    }

    private async Task FetchFile(string baseUrl, string filename, string destination, Action<long?, long>? onProgress = null)
    {
        var baseUri = new Uri(baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
        var fullUri = new Uri(baseUri, filename);

        if (fullUri.Scheme == "file")
        {
            var sourcePath = fullUri.LocalPath;
            if (!File.Exists(sourcePath)) throw new FileNotFoundException($"File not found: {sourcePath}");
            File.Copy(sourcePath, destination, overwrite: true);
            var info = new FileInfo(sourcePath);
            onProgress?.Invoke(info.Length, info.Length);
        }
        else
        {
            var request = new HttpRequestMessage(HttpMethod.Get, fullUri) { Version = HttpVersion.Version11 };
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