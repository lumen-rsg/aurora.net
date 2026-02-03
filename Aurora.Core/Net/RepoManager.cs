using Aurora.Core.Contract;
using Aurora.Core.IO;
using Aurora.Core.Logging;
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
        _client = new HttpClient();
        _client.Timeout = TimeSpan.FromSeconds(30);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Aurora/1.0");
    }

    public async Task SyncRepositoriesAsync(Action<string, string> onProgress)
    {
        // 1. Load the new 'repolist' config file
        var configPath = PathHelper.GetPath(_rootPath, "etc/aurora/repolist");
        if (!File.Exists(configPath))
        {
            AuLogger.Info("No repositories configured at etc/aurora/repolist");
            return;
        }
        var repos = RepoConfigParser.Parse(File.ReadAllText(configPath));

        var dbDir = PathHelper.GetPath(_rootPath, "var/lib/aurora");
        Directory.CreateDirectory(dbDir);
        var gpgHome = PathHelper.GetPath(_rootPath, "etc/aurora/gnupg");

        // 2. Iterate through configured repos
        foreach (var repo in repos.Values.Where(r => r.Enabled))
        {
            var repoFileName = $"{repo.Id}.aurepo";
            var targetFile = Path.Combine(dbDir, repoFileName);
            var sigFile = targetFile + ".asc";

            onProgress(repo.Name, "Syncing...");

            try
            {
                // 3. Download the .aurepo file and its signature
                await FetchFile(repo.Url, repoFileName, targetFile);
                await FetchFile(repo.Url, repoFileName + ".asc", sigFile);

                // 4. Verify Signature (if not skipped)
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

    private async Task FetchFile(string baseUrl, string filename, string destination)
    {
        // Use Uri constructor to handle slashes correctly
        // Base: https://packages.lumina.1t.ru/core/
        // File: core.aurepo
        var baseUri = new Uri(baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
        var fullUri = new Uri(baseUri, filename);

        if (fullUri.Scheme == "file")
        {
            var sourcePath = fullUri.LocalPath;
            if (!File.Exists(sourcePath)) throw new FileNotFoundException($"Remote file not found: {sourcePath}");
            File.Copy(sourcePath, destination, overwrite: true);
        }
        else
        {
            // Use GetStreamAsync for better memory handling of large repo files
            using var response = await _client.GetStreamAsync(fullUri);
            using var fs = new FileStream(destination, FileMode.Create);
            await response.CopyToAsync(fs);
        }
    }

    // DownloadPackageAsync from previous steps remains the same, as it deals with .au files
    public async Task<string?> DownloadPackageAsync(Aurora.Core.Models.Package pkg, string cacheDir, Action<string> onProgress)
    {
        // (Keep existing DownloadPackageAsync implementation here)
        var filename = $"{pkg.Name}-{pkg.Version}-{pkg.Arch}.au";
        // ... rest of the download logic ...
        return null;
    }
}