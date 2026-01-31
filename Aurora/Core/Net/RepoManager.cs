using Aurora.Core.IO;
using Aurora.Core.Logging;
using Aurora.Core.Models;
using Aurora.Core.Security;

namespace Aurora.Core.Net;

public class RepoManager
{
    private readonly string _rootPath;
    private readonly HttpClient _client;

    public RepoManager(string rootPath)
    {
        _rootPath = rootPath;
        _client = new HttpClient();
        // Set a reasonable timeout
        _client.Timeout = TimeSpan.FromSeconds(30);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Aurora/1.0");
    }
    
    public bool SkipSignatureCheck { get; set; } = false;

    public Dictionary<string, string> LoadRepoConfig()
    {
        var configPath = PathHelper.GetPath(_rootPath, "etc/aurora/repolist");
        var repos = new Dictionary<string, string>();

        if (!File.Exists(configPath))
            return repos;

        foreach (var line in File.ReadAllLines(configPath))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#')) continue;

            var parts = trimmed.Split('=', 2);
            if (parts.Length == 2)
            {
                var name = parts[0].Trim();
                var url = parts[1].Trim();
                repos[name] = url;
            }
        }
        return repos;
    }
    
    // Add to RepoManager class
    
    public async Task<string?> DownloadPackageAsync(Aurora.Core.Models.Package pkg, string cacheDir, Action<string> onProgress)
    {
        var filename = $"{pkg.Name}-{pkg.Version}-{pkg.Arch}.au";
        var cachePath = Path.Combine(cacheDir, filename);

        // 1. Check Cache with Strict Integrity
        if (File.Exists(cachePath))
        {
            if (!string.IsNullOrEmpty(pkg.Checksum))
            {
                var cachedHash = HashHelper.ComputeFileHash(cachePath);
                if (cachedHash == pkg.Checksum)
                {
                    // Cache is valid
                    return cachePath;
                }
                else
                {
                    onProgress($"[yellow]Cache corruption detected for {pkg.Name}. Re-downloading.[/]");
                    File.Delete(cachePath);
                }
            }
            else
            {
                // No checksum provided by repo? 
                // In strict mode we might reject this, but for now we accept it with a warning.
                onProgress("[yellow]Warning: No checksum for cached package.[/]");
                return cachePath;
            }
        }

        Directory.CreateDirectory(cacheDir);

        // 2. Try all configured repos
        var repos = LoadRepoConfig();
        
        foreach (var repo in repos)
        {
            var baseUrl = repo.Value.TrimEnd('/');
            bool downloadSuccess = false;
            
            try
            {
                if (baseUrl.StartsWith("file://"))
                {
                    var sourcePath = Path.Combine(baseUrl.Substring(7), filename);
                    if (File.Exists(sourcePath))
                    {
                        onProgress($"Found in {repo.Key} (Local)");
                        File.Copy(sourcePath, cachePath, overwrite: true);
                        downloadSuccess = true;
                    }
                }
                else
                {
                    var url = $"{baseUrl}/{filename}";
                    onProgress($"Downloading from {repo.Key}...");
                    
                    var data = await _client.GetByteArrayAsync(url);
                    await File.WriteAllBytesAsync(cachePath, data);
                    downloadSuccess = true;
                }
            }
            catch
            {
                // Continue to next repo
            }

            // 3. Verify Download
            if (downloadSuccess)
            {
                if (!string.IsNullOrEmpty(pkg.Checksum))
                {
                    var downloadedHash = HashHelper.ComputeFileHash(cachePath);
                    if (downloadedHash != pkg.Checksum)
                    {
                        File.Delete(cachePath); // Security: Delete immediately
                        throw new InvalidDataException($"Security Error: Checksum mismatch for {pkg.Name}.\nExpected: {pkg.Checksum}\nActual:   {downloadedHash}");
                    }
                    onProgress($"Verified ({pkg.Checksum.Substring(0,8)})");
                }
                else
                {
                    onProgress("[yellow]Security Warning: Package has no checksum signature.[/]");
                }
                
                return cachePath;
            }
        }

        return null; 
    }

    public async Task SyncRepositoriesAsync(Action<string, string> onProgress)
    {
        var repos = LoadRepoConfig();
        if (repos.Count == 0)
        {
            AuLogger.Info("No repositories configured in etc/aurora/repolist");
            return;
        }

        var dbDir = PathHelper.GetPath(_rootPath, "var/lib/aurora");
        Directory.CreateDirectory(dbDir);
        
        // Use a GPG Home dir inside sysroot for isolation during tests/bootstrap
        var gpgHome = PathHelper.GetPath(_rootPath, "etc/aurora/gnupg");
        
        

        foreach (var repo in repos)
        {
            var name = repo.Key;
            var baseUrl = repo.Value.TrimEnd('/');
            var targetFile = Path.Combine(dbDir, $"{name}.yaml");
            var sigFile = targetFile + ".asc"; // repo.yaml.asc
            
            if (SkipSignatureCheck)
            {
                onProgress(name, "Done (Unverified)");
            }
            else
            {

                onProgress(name, "Downloading...");

                try
                {
                    // 1. Download Metadata (YAML)
                    await FetchFile(baseUrl, "repo.yaml", targetFile);

                    // 2. Download Signature (.asc)
                    try
                    {
                        await FetchFile(baseUrl, "repo.yaml.asc", sigFile);
                    }
                    catch
                    {
                        // If signature is missing, fail strictly?
                        throw new Exception("Signature file (repo.yaml.asc) missing.");
                    }

                    // 3. Verify Signature
                    onProgress(name, "Verifying GPG...");

                    bool valid = GpgHelper.VerifySignature(targetFile, sigFile,
                        Directory.Exists(gpgHome) ? gpgHome : null);

                    if (!valid)
                    {
                        // SECURITY FAILURE: Delete the untrusted file
                        File.Delete(targetFile);
                        File.Delete(sigFile);
                        throw new Exception("Invalid GPG Signature! Repository is untrusted.");
                    }

                    onProgress(name, "Done (Signed)");
                }

                catch (Exception ex)
                {
                    AuLogger.Error($"Failed to sync {name}: {ex.Message}");
                    onProgress(name, $"Failed: {ex.Message}");
                }
            }
        }
    }
    
    private async Task FetchFile(string baseUrl, string filename, string destination)
    {
        if (baseUrl.StartsWith("file://"))
        {
            var sourcePath = Path.Combine(baseUrl.Substring(7), filename);
            if (!File.Exists(sourcePath)) throw new FileNotFoundException($"Remote file not found: {sourcePath}");
            File.Copy(sourcePath, destination, overwrite: true);
        }
        else
        {
            var url = $"{baseUrl}/{filename}";
            var data = await _client.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(destination, data);
        }
    }
}