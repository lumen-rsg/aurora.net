using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using Spectre.Console;

namespace Aurora.Core.Net;

public class RepoManager
{
    private readonly string _rootPath;
    private readonly HttpClient _client;
    public bool SkipSignatureCheck { get; set; } = false;

    public RepoManager(string rootPath)
    {
        _rootPath = rootPath;

        // Force IPv4 to prevent 30-second timeouts on misconfigured mirrors
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
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Aurora Package Manager)");
    }

    public async Task SyncRepositoriesAsync(Action<string, string> onProgress)
    {
        var reposDir = PathHelper.GetPath(_rootPath, "etc/yum.repos.d");
       
        // FIX: Correctly scope the if-statement
        if (!Directory.Exists(reposDir)) 
        {
            AuLogger.Error("No repositories configured at etc/yum.repos.d");
            return;
        }

        var repos = RepoConfigParser.ParseDirectory(reposDir);
        if (repos.Count == 0) return;

        var dbDir = PathHelper.GetPath(_rootPath, "var/lib/aurora");
        Directory.CreateDirectory(dbDir);
        var gpgHome = PathHelper.GetPath(_rootPath, "etc/aurora/gnupg");

        foreach (var repo in repos.Values.Where(r => r.Enabled))
        {
            onProgress(repo.Name, "Syncing repomd.xml...");

            try
            {
                // 1. Fetch repomd.xml
                var repomdPath = Path.Combine(dbDir, $"{repo.Id}_repomd.xml");
                await FetchFile(repo.Url, "repodata/repomd.xml", repomdPath);

                // 2. Verify Signature (if provided)
                if (!SkipSignatureCheck)
                {
                    var sigPath = repomdPath + ".asc";
                    try
                    {
                        await FetchFile(repo.Url, "repodata/repomd.xml.asc", sigPath);
                        if (!GpgHelper.VerifySignature(repomdPath, sigPath, Directory.Exists(gpgHome) ? gpgHome : null))
                        {
                            throw new Exception("Invalid GPG Signature for repomd.xml!");
                        }
                    }
                    catch (Exception ex) when (!ex.Message.Contains("Signature"))
                    {
                        AuLogger.Debug($"No repomd.xml.asc found for {repo.Name}, skipping signature check.");
                    }
                }

                // 3. Parse repomd.xml to find the primary SQLite DB
                var xmlContent = await File.ReadAllTextAsync(repomdPath);
                var primaryDbInfo = RepoMdParser.GetPrimaryDbInfo(xmlContent);

                if (primaryDbInfo == null || string.IsNullOrEmpty(primaryDbInfo.Location))
                {
                    throw new Exception("Could not locate primary_db in repomd.xml");
                }

                onProgress(repo.Name, "Downloading primary database...");

                // 4. Download the compressed SQLite DB
                string extension = Path.GetExtension(primaryDbInfo.Location); // usually .gz, .bz2, or .zst
                var compressedDbPath = Path.Combine(dbDir, $"{repo.Id}_primary{extension}");
                await FetchFile(repo.Url, primaryDbInfo.Location, compressedDbPath);

                // 5. Decompress into the final SQLite file
                onProgress(repo.Name, "Decompressing...");
                var targetSqliteFile = Path.Combine(dbDir, $"{repo.Id}.sqlite");
                await DecompressDatabaseAsync(compressedDbPath, targetSqliteFile);

                // Cleanup
                File.Delete(compressedDbPath);
                File.Delete(repomdPath);

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
        if (string.IsNullOrEmpty(pkg.LocationHref)) throw new ArgumentException($"Package {pkg.Name} lacks a LocationHref.");

        var filename = Path.GetFileName(pkg.LocationHref);
        var cachePath = Path.Combine(cacheDir, filename);

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

        var reposDir = PathHelper.GetPath(_rootPath, "etc/yum.repos.d");
        if (!Directory.Exists(reposDir)) throw new Exception("No repolist directory found.");
        var repos = RepoConfigParser.ParseDirectory(reposDir);
        
        foreach (var repo in repos.Values.Where(r => r.Enabled))
        {
            try
            {
                // DEBUG: Print the attempt
                AnsiConsole.MarkupLine($"[grey]Trying {repo.Name} for {pkg.Name}...[/]");
                
                await FetchFile(repo.Url, pkg.LocationHref, cachePath, onProgress);

                if (!string.IsNullOrEmpty(pkg.Checksum))
                {
                    var actualSum = HashHelper.ComputeFileHash(cachePath);
                    if (!string.Equals(actualSum, pkg.Checksum, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(cachePath);
                        throw new InvalidDataException("Integrity mismatch.");
                    }
                }
                return cachePath;
            }
            catch (HttpRequestException)
            {
                // Standard 404, package not in this specific repo. Move to next.
                if (File.Exists(cachePath)) File.Delete(cachePath);
            }
            catch (Exception ex)
            {
                AuLogger.Debug($"Mirror {repo.Name} failed for {filename}: {ex.Message}");
                if (File.Exists(cachePath)) File.Delete(cachePath);
            }
        }

        return null;
    }

    private async Task FetchFile(string baseUrl, string relativePath, string destination, Action<long?, long>? onProgress = null)
    {
        var baseUriString = baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/";
        var baseUri = new Uri(baseUriString);
        var escapedFilename = relativePath.Replace(":", "%3A"); // Handle epochs
        var fullUri = new Uri(baseUri, escapedFilename);

        // --- CRITICAL DEBUG LINE ---
        // Uncomment this if the next run still fails, it will print the exact URL
        AuLogger.Info($"Fetching: {fullUri.AbsoluteUri}");

        using var request = new HttpRequestMessage(HttpMethod.Get, fullUri) { Version = HttpVersion.Version11 };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        
        response.EnsureSuccessStatusCode(); // Throws on 404

        var totalBytes = response.Content.Headers.ContentLength;
        await using var downloadStream = await response.Content.ReadAsStreamAsync();
        
        // Open file ONLY if we got a 200 OK
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

    private async Task DecompressDatabaseAsync(string compressedFile, string outputFile)
    {
        var ext = Path.GetExtension(compressedFile).ToLowerInvariant();
        ProcessStartInfo psi;

        // Route decompression based on Fedora/RPM standards
        if (ext == ".zst" || ext == ".zstd")
        {
            psi = new ProcessStartInfo("zstd", $"-d -q \"{compressedFile}\" -o \"{outputFile}\" -f");
        }
        else if (ext == ".gz")
        {
            psi = new ProcessStartInfo("/bin/sh", $"-c \"gzip -d -c '{compressedFile}' > '{outputFile}'\"");
        }
        else if (ext == ".bz2")
        {
            psi = new ProcessStartInfo("/bin/sh", $"-c \"bzip2 -d -c '{compressedFile}' > '{outputFile}'\"");
        }
        else
        {
            File.Copy(compressedFile, outputFile, true);
            return;
        }

        psi.CreateNoWindow = true;
        psi.UseShellExecute = false;
        psi.RedirectStandardError = true;

        using var proc = Process.Start(psi);
        if (proc == null) throw new Exception("Failed to start decompression process.");

        string error = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
        {
            throw new Exception($"Database decompression failed: {error}");
        }
    }
}