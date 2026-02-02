using System.Diagnostics;
using Aurora.Core.Models;

namespace Aurora.Core.Net.DownloadProviders;

public class GitProvider : IDownloadProvider
{
    public string[] SupportedProtocols => new[] { "git" };

    public async Task DownloadAsync(SourceEntry entry, string destinationPath, Action<long?, long> onProgress)
    {
        // 1. Clean the URL
        var cloneUrl = entry.Url.StartsWith("git+") ? entry.Url.Substring(4) : entry.Url;
        
        // Remove fragments (e.g. #tag=v1.0)
        if (cloneUrl.Contains('#')) cloneUrl = cloneUrl.Split('#')[0];
        
        // FIX: Remove query parameters (e.g. ?signed)
        if (cloneUrl.Contains('?')) cloneUrl = cloneUrl.Split('?')[0];

        // 2. Signal start
        onProgress(null, 0);

        if (!Directory.Exists(destinationPath))
        {
            await RunGitCommandAsync($"clone --mirror \"{cloneUrl}\" \"{destinationPath}\"");
        }
        else
        {
            await RunGitCommandAsync($"remote set-url origin \"{cloneUrl}\"", destinationPath);
            await RunGitCommandAsync($"remote update --prune", destinationPath);
        }

        onProgress(100, 100);
    }

    private async Task RunGitCommandAsync(string arguments, string? workingDir = null)
    {
        var psi = new ProcessStartInfo {
            FileName = "git", 
            Arguments = arguments,
            UseShellExecute = false, 
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        
        if (workingDir != null) psi.WorkingDirectory = workingDir;

        using var proc = Process.Start(psi);
        if (proc == null) throw new Exception("Failed to start git.");
        
        var err = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        // Git often prints progress to stderr, but if exit code is 0, it's not an error.
        if (proc.ExitCode != 0) throw new Exception($"Git error: {err}");
    }
}