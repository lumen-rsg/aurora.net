using System.Diagnostics;
using Aurora.Core.Models;
using Aurora.Core.Logging;

namespace Aurora.Core.Net.DownloadProviders;

public class GitProvider : IDownloadProvider
{
    // Handle both git:// and git+https:// (parsed as 'git')
    public string[] SupportedProtocols => new[] { "git" };

    public async Task DownloadAsync(SourceEntry entry, string destinationPath, Action<string> onProgress)
    {
        var cloneUrl = entry.Url.StartsWith("git+") ? entry.Url.Substring(4) : entry.Url;
        if (cloneUrl.Contains('#')) cloneUrl = cloneUrl.Split('#')[0];

        if (!Directory.Exists(destinationPath))
        {
            onProgress($"Cloning {entry.FileName} mirror...");
            // Use --mirror to get all refs/tags/branches
            await RunGitCommandAsync($"clone --mirror \"{cloneUrl}\" \"{destinationPath}\"");
        }
        else
        {
            onProgress($"Updating {entry.FileName} mirror...");
            // Correct way to update a bare mirror
            await RunGitCommandAsync($"remote update --prune", destinationPath);
        }
    }

    private async Task RunGitCommandAsync(string arguments, string? workingDir = null)
    {
        var psi = new ProcessStartInfo {
            FileName = "git", Arguments = arguments,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true
        };
        if (workingDir != null) psi.WorkingDirectory = workingDir;
        using var proc = Process.Start(psi);
        var err = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0) throw new Exception($"Git error: {err}");
    }
}