using System.Diagnostics;
using Aurora.Core.Models;
using Spectre.Console;

namespace Aurora.Core.Net.DownloadProviders;

public class GitProvider : IDownloadProvider
{
    public string[] SupportedProtocols => new[] { "git" };

    public async Task DownloadAsync(SourceEntry entry, string destinationPath, Action<long?, long> onProgress)
    {
        var cloneUrl = entry.Url.StartsWith("git+") ? entry.Url.Substring(4) : entry.Url;
        if (cloneUrl.Contains('#')) cloneUrl = cloneUrl.Split('#')[0];
        if (cloneUrl.Contains('?')) cloneUrl = cloneUrl.Split('?')[0];

        onProgress(null, 0);

        int maxRetries = 3;
        for (int i = 1; i <= maxRetries; i++)
        {
            try
            {
                if (!Directory.Exists(destinationPath))
                {
                    await RunGitCommandAsync($"clone --mirror \"{cloneUrl}\" \"{destinationPath}\"");
                }
                else
                {
                    await RunGitCommandAsync($"remote set-url origin \"{cloneUrl}\"", destinationPath);
                    await RunGitCommandAsync($"remote update --prune", destinationPath);
                }
                
                // Success!
                onProgress(100, 100);
                return;
            }
            catch (Exception ex)
            {
                if (i == maxRetries)
                {
                    // Last attempt failed, throw for real
                    throw;
                }
                AnsiConsole.MarkupLine($"[yellow]Git operation failed (Attempt {i}/{maxRetries}): {ex.Message}. Retrying...[/]");
                // Clean up partially failed clone
                if (Directory.Exists(destinationPath)) Directory.Delete(destinationPath, true);
                await Task.Delay(2000); // Wait 2 seconds before retry
            }
        }
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