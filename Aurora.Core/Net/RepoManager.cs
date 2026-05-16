using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
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

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MirrorResolveTimeout = TimeSpan.FromSeconds(30);

    // Per-session mirror resolution cache: repoId -> resolved base URL
    private readonly Dictionary<string, string> _resolvedUrls = new();

    public RepoManager(string rootPath)
    {
        _rootPath = rootPath;

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = async (context, cancellationToken) =>
            {
                using var dnsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                dnsCts.CancelAfter(ConnectTimeout);

                var entry = await Dns.GetHostEntryAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, dnsCts.Token);
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(new IPEndPoint(entry.AddressList[0], context.DnsEndPoint.Port), dnsCts.Token);
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
            onProgress(repo.Name, "Resolving mirror...");

            try
            {
                var baseUrl = await ResolveRepoUrlAsync(repo);

                // 1. Fetch repomd.xml
                onProgress(repo.Name, "Syncing repomd.xml...");
                var repomdPath = Path.Combine(dbDir, $"{repo.Id}_repomd.xml");
                await FetchFile(baseUrl, "repodata/repomd.xml", repomdPath);

                // 2. Verify GPG signature
                if (!SkipSignatureCheck)
                {
                    var sigPath = repomdPath + ".asc";
                    try
                    {
                        await FetchFile(baseUrl, "repodata/repomd.xml.asc", sigPath);
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

                // 3. Parse repomd.xml — get all data refs at once
                var xmlContent = await File.ReadAllTextAsync(repomdPath);
                var allRefs = RepoMdParser.GetAllDataRefs(xmlContent);

                // 4. Download primary metadata (prefer primary_db, fall back to primary.xml)
                var primaryRef = allRefs.GetValueOrDefault("primary_db") ?? allRefs.GetValueOrDefault("primary");
                if (primaryRef == null || string.IsNullOrEmpty(primaryRef.Location))
                    throw new Exception("Could not locate primary_db or primary in repomd.xml");

                onProgress(repo.Name, $"Downloading primary ({primaryRef.Type})...");
                var isPrimaryXml = primaryRef.Type == "primary";
                var primaryExt = Path.GetExtension(primaryRef.Location);
                var compressedPrimary = Path.Combine(dbDir, $"{repo.Id}_primary{primaryExt}.tmp");
                await FetchFile(baseUrl, primaryRef.Location, compressedPrimary);

                // Verify compressed checksum
                if (!SkipSignatureCheck && !string.IsNullOrEmpty(primaryRef.ChecksumValue))
                {
                    if (!HashHelper.VerifyFile(compressedPrimary, primaryRef.ChecksumValue, primaryRef.ChecksumType))
                    {
                        File.Delete(compressedPrimary);
                        throw new InvalidDataException($"Checksum mismatch for {primaryRef.Type}");
                    }
                }

                // Decompress
                onProgress(repo.Name, "Decompressing...");
                var primaryTarget = isPrimaryXml
                    ? Path.Combine(dbDir, $"{repo.Id}_primary.xml")
                    : Path.Combine(dbDir, $"{repo.Id}.sqlite");
                await DecompressDatabaseAsync(compressedPrimary, primaryTarget, primaryExt);

                // Verify open checksum (decompressed content)
                if (!SkipSignatureCheck && !string.IsNullOrEmpty(primaryRef.OpenChecksumValue))
                {
                    if (!HashHelper.VerifyFile(primaryTarget, primaryRef.OpenChecksumValue, primaryRef.OpenChecksumType))
                    {
                        File.Delete(primaryTarget);
                        throw new InvalidDataException($"Open checksum mismatch for {primaryRef.Type}");
                    }
                }

                File.Delete(compressedPrimary);

                // 5. Download filelists (optional)
                var filelistsRef = allRefs.GetValueOrDefault("filelists_db") ?? allRefs.GetValueOrDefault("filelists");
                if (filelistsRef != null && !string.IsNullOrEmpty(filelistsRef.Location))
                {
                    onProgress(repo.Name, "Downloading filelists...");
                    var flExt = Path.GetExtension(filelistsRef.Location);
                    var isFilelistsXml = filelistsRef.Type == "filelists";
                    var compressedFl = Path.Combine(dbDir, $"{repo.Id}_filelists{flExt}.tmp");
                    try
                    {
                        await FetchFile(baseUrl, filelistsRef.Location, compressedFl);

                        if (!SkipSignatureCheck && !string.IsNullOrEmpty(filelistsRef.ChecksumValue))
                        {
                            if (!HashHelper.VerifyFile(compressedFl, filelistsRef.ChecksumValue, filelistsRef.ChecksumType))
                            {
                                File.Delete(compressedFl);
                                throw new InvalidDataException($"Checksum mismatch for {filelistsRef.Type}");
                            }
                        }

                        var flTarget = isFilelistsXml
                            ? Path.Combine(dbDir, $"{repo.Id}_filelists.xml")
                            : Path.Combine(dbDir, $"{repo.Id}_filelists.sqlite");
                        await DecompressDatabaseAsync(compressedFl, flTarget, flExt);
                        File.Delete(compressedFl);
                    }
                    catch (Exception ex)
                    {
                        AuLogger.Debug($"Failed to download filelists for {repo.Name}: {ex.Message}");
                        try { File.Delete(compressedFl); } catch { }
                    }
                }

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

    public async Task SyncCompsAsync(Action<string, string> onProgress)
    {
        var reposDir = PathHelper.GetPath(_rootPath, "etc/yum.repos.d");
        if (!Directory.Exists(reposDir)) return;

        var repos = RepoConfigParser.ParseDirectory(reposDir);
        if (repos.Count == 0) return;

        var dbDir = PathHelper.GetPath(_rootPath, "var/lib/aurora");
        Directory.CreateDirectory(dbDir);

        foreach (var repo in repos.Values.Where(r => r.Enabled))
        {
            try
            {
                var baseUrl = await ResolveRepoUrlAsync(repo);

                var repomdPath = Path.Combine(dbDir, $"{repo.Id}_repomd_comps.xml");
                await FetchFile(baseUrl, "repodata/repomd.xml", repomdPath);
                var xmlContent = await File.ReadAllTextAsync(repomdPath);
                var groupInfo = RepoMdParser.GetGroupInfo(xmlContent);

                if (groupInfo == null || string.IsNullOrEmpty(groupInfo.Location))
                {
                    File.Delete(repomdPath);
                    continue;
                }

                onProgress(repo.Name, "Downloading comps...");

                var compressedCompsPath = Path.Combine(dbDir, $"{repo.Id}_comps.xml.tmp");
                await FetchFile(baseUrl, groupInfo.Location, compressedCompsPath);

                // Verify checksum
                if (!SkipSignatureCheck && !string.IsNullOrEmpty(groupInfo.ChecksumValue))
                {
                    if (!HashHelper.VerifyFile(compressedCompsPath, groupInfo.ChecksumValue, groupInfo.ChecksumType))
                    {
                        File.Delete(compressedCompsPath);
                        File.Delete(repomdPath);
                        throw new InvalidDataException("Checksum mismatch for comps");
                    }
                }

                var compsXmlPath = Path.Combine(dbDir, $"{repo.Id}_comps.xml");
                if (groupInfo.Location.EndsWith(".gz") || groupInfo.Location.EndsWith(".bz2") || groupInfo.Location.EndsWith(".zst"))
                {
                    var compsExt = Path.GetExtension(groupInfo.Location);
                    await DecompressDatabaseAsync(compressedCompsPath, compsXmlPath, compsExt);
                    File.Delete(compressedCompsPath);
                }
                else
                {
                    File.Move(compressedCompsPath, compsXmlPath, overwrite: true);
                }

                var compsXml = await File.ReadAllTextAsync(compsXmlPath);
                var (groups, categories) = CompsParser.Parse(compsXml);

                foreach (var g in groups) g.RepoId = repo.Id;
                foreach (var c in categories) c.RepoId = repo.Id;

                var jsonData = JsonSerializer.Serialize(new CompsData { Groups = groups, Categories = categories },
                    new JsonSerializerOptions { WriteIndented = true });

                var jsonPath = Path.Combine(dbDir, $"{repo.Id}_comps.json");
                await File.WriteAllTextAsync(jsonPath, jsonData);

                File.Delete(compsXmlPath);
                File.Delete(repomdPath);

                onProgress(repo.Name, $"Comps synced ({groups.Count} groups)");
            }
            catch (Exception ex)
            {
                AuLogger.Error($"Failed to sync comps for '{repo.Name}': {ex.Message}");
            }
        }
    }

    public static List<PackageGroup> LoadAllGroups(string sysRoot)
    {
        var dbDir = PathHelper.GetPath(sysRoot, "var/lib/aurora");
        if (!Directory.Exists(dbDir)) return new List<PackageGroup>();

        var allGroups = new List<PackageGroup>();

        foreach (var jsonFile in Directory.GetFiles(dbDir, "*_comps.json"))
        {
            try
            {
                var json = File.ReadAllText(jsonFile);
                var data = JsonSerializer.Deserialize<CompsData>(json);
                if (data?.Groups != null)
                    allGroups.AddRange(data.Groups);
            }
            catch (Exception ex)
            {
                AuLogger.Error($"Failed to load comps from {jsonFile}: {ex.Message}");
            }
        }

        return allGroups;
    }

    public static List<PackageCategory> LoadAllCategories(string sysRoot)
    {
        var dbDir = PathHelper.GetPath(sysRoot, "var/lib/aurora");
        if (!Directory.Exists(dbDir)) return new List<PackageCategory>();

        var allCategories = new List<PackageCategory>();

        foreach (var jsonFile in Directory.GetFiles(dbDir, "*_comps.json"))
        {
            try
            {
                var json = File.ReadAllText(jsonFile);
                var data = JsonSerializer.Deserialize<CompsData>(json);
                if (data?.Categories != null)
                    allCategories.AddRange(data.Categories);
            }
            catch (Exception ex)
            {
                AuLogger.Error($"Failed to load comps from {jsonFile}: {ex.Message}");
            }
        }

        return allCategories;
    }

    private class CompsData
    {
        public List<PackageGroup> Groups { get; set; } = new();
        public List<PackageCategory> Categories { get; set; } = new();
    }

    public async Task<string?> DownloadPackageAsync(Package pkg, string cacheDir, Action<long?, long> onProgress)
    {
        if (string.IsNullOrEmpty(pkg.LocationHref)) throw new ArgumentException($"Package {pkg.Name} lacks a LocationHref.");

        var filename = Path.GetFileName(pkg.LocationHref);
        var cachePath = Path.Combine(cacheDir, filename);

        if (File.Exists(cachePath))
        {
            if (string.IsNullOrEmpty(pkg.Checksum) ||
                HashHelper.VerifyFile(cachePath, pkg.Checksum, string.IsNullOrEmpty(pkg.ChecksumType) ? "sha256" : pkg.ChecksumType))
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

        if (!repos.TryGetValue(pkg.RepositoryId, out var targetRepo) || !targetRepo.Enabled)
        {
            throw new Exception($"Cannot download {pkg.Name}: Repository '{pkg.RepositoryId}' is missing or disabled.");
        }

        try
        {
            var baseUrl = await ResolveRepoUrlAsync(targetRepo);

            // Strip redundant repo-id prefix from location href
            string safeHref = pkg.LocationHref;
            string repoNameSegment = pkg.RepositoryId + "/";
            if (safeHref.StartsWith(repoNameSegment))
                safeHref = safeHref.Substring(repoNameSegment.Length);

            await FetchFile(baseUrl, safeHref, cachePath, onProgress);

            if (!string.IsNullOrEmpty(pkg.Checksum))
            {
                var algo = string.IsNullOrEmpty(pkg.ChecksumType) ? "sha256" : pkg.ChecksumType;
                if (!HashHelper.VerifyFile(cachePath, pkg.Checksum, algo))
                {
                    File.Delete(cachePath);
                    throw new InvalidDataException("Integrity mismatch.");
                }
            }
            return cachePath;
        }
        catch (HttpRequestException)
        {
            if (File.Exists(cachePath)) File.Delete(cachePath);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]DEBUG Error for {pkg.Name}: {ex.Message}[/]");
            if (File.Exists(cachePath)) File.Delete(cachePath);
        }

        return null;
    }

    // --- Mirror Resolution ---

    private async Task<string> ResolveRepoUrlAsync(Aurora.Core.Contract.RepoConfig repo)
    {
        // Check session cache
        if (_resolvedUrls.TryGetValue(repo.Id, out var cached))
            return cached;

        string resolved;

        // 1. Direct baseurl (fast path)
        if (!string.IsNullOrEmpty(repo.BaseUrl))
        {
            resolved = repo.BaseUrl;
        }
        // 2. Metalink
        else if (!string.IsNullOrEmpty(repo.Metalink))
        {
            resolved = await ResolveMetalinkAsync(repo.Metalink, repo.Id);
        }
        // 3. Mirrorlist
        else if (!string.IsNullOrEmpty(repo.Mirrorlist))
        {
            resolved = await ResolveMirrorlistAsync(repo.Mirrorlist, repo.Id);
        }
        else
        {
            throw new Exception($"Repository '{repo.Id}' has no baseurl, metalink, or mirrorlist configured.");
        }

        repo.EffectiveUrl = resolved;
        _resolvedUrls[repo.Id] = resolved;
        return resolved;
    }

    private async Task<string> ResolveMetalinkAsync(string metalinkUrl, string repoId)
    {
        try
        {
            using var resolveCts = new CancellationTokenSource(MirrorResolveTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, metalinkUrl) { Version = HttpVersion.Version11 };
            using var response = await _client.SendAsync(request, resolveCts.Token);
            response.EnsureSuccessStatusCode();

            var xml = await response.Content.ReadAsStringAsync();
            var urls = MetalinkParser.ParseMirrorUrls(xml);

            if (urls.Count == 0)
                throw new Exception("Metalink returned no mirrors");

            // Test first mirror with a HEAD request
            foreach (var url in urls)
            {
                try
                {
                    using var probeCts = new CancellationTokenSource(MirrorResolveTimeout);
                    var testUri = BuildUri(url, "repodata/repomd.xml");
                    using var headReq = new HttpRequestMessage(HttpMethod.Head, testUri) { Version = HttpVersion.Version11 };
                    using var headResp = await _client.SendAsync(headReq, probeCts.Token);
                    if (headResp.IsSuccessStatusCode)
                        return url.TrimEnd('/') + "/";
                }
                catch { /* try next mirror */ }
            }

            // If all HEAD requests fail, return first URL anyway
            return urls[0].TrimEnd('/') + "/";
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to resolve metalink for {repoId}: {ex.Message}");
        }
    }

    private async Task<string> ResolveMirrorlistAsync(string mirrorlistUrl, string repoId)
    {
        try
        {
            using var resolveCts = new CancellationTokenSource(MirrorResolveTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, mirrorlistUrl) { Version = HttpVersion.Version11 };
            using var response = await _client.SendAsync(request, resolveCts.Token);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            var urls = MirrorlistParser.ParseMirrorlist(content);

            if (urls.Count == 0)
                throw new Exception("Mirrorlist returned no mirrors");

            return urls[0].TrimEnd('/') + "/";
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to resolve mirrorlist for {repoId}: {ex.Message}");
        }
    }

    // --- HTTP Helpers ---

    private static Uri BuildUri(string baseUrl, string relativePath)
    {
        var baseUri = new Uri(baseUrl.TrimEnd('/') + "/");
        return new Uri(baseUri, relativePath.TrimStart('/'));
    }

    private async Task FetchFile(string baseUrl, string relativePath, string destination, Action<long?, long>? onProgress = null)
    {
        var fullUri = BuildUri(baseUrl, relativePath);

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

    private async Task DecompressDatabaseAsync(string compressedFile, string outputFile, string? originalExtension = null)
    {
        var ext = (originalExtension ?? Path.GetExtension(compressedFile)).ToLowerInvariant();
        ProcessStartInfo psi;

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
        else if (ext == ".xz")
        {
            psi = new ProcessStartInfo("/bin/sh", $"-c \"xz -d -c '{compressedFile}' > '{outputFile}'\"");
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
