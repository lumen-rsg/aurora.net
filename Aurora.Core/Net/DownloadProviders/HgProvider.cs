using System.Diagnostics;
using Aurora.Core.Models;
using Spectre.Console;

namespace Aurora.Core.Net.DownloadProviders;

public class HgProvider : IDownloadProvider
{
    public string[] SupportedProtocols => new[] { "hg" };

    public async Task DownloadAsync(SourceEntry entry, string destinationPath, Action<long?, long> onProgress)
    {
        // Clean the URL, removing the 'hg+' prefix if it exists
        var cloneUrl = entry.Url.StartsWith("hg+") ? entry.Url.Substring(3) : entry.Url;
        
        // Remove fragment for cloning (e.g., #rev=default)
        if (cloneUrl.Contains('#')) cloneUrl = cloneUrl.Split('#')[0];

        // Mercurial doesn't provide easy progress, so we use an indeterminate spinner
        onProgress(null, 0);

        if (!Directory.Exists(destinationPath))
        {
            // Initial Clone
            AnsiConsole.MarkupLine($"[grey]Cloning Mercurial repository: {entry.FileName}...[/]");
            await RunHgCommandAsync($"clone \"{cloneUrl}\" \"{destinationPath}\"");
        }
        else
        {
            // Subsequent Pull
            AnsiConsole.MarkupLine($"[grey]Updating Mercurial repository: {entry.FileName}...[/]");
            await RunHgCommandAsync("pull", destinationPath);
        }
    }

    private async Task RunHgCommandAsync(string arguments, string? workingDir = null)
    {
        var psi = new ProcessStartInfo {
            FileName = "hg", 
            Arguments = arguments,
            UseShellExecute = false, 
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        
        if (workingDir != null) psi.WorkingDirectory = workingDir;

        using var proc = Process.Start(psi);
        if (proc == null) throw new Exception("Failed to start Mercurial (hg) process. Is it installed and in your PATH?");
        
        // We read both streams to prevent the process from hanging
        var err = await proc.StandardError.ReadToEndAsync();
        var output = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
        {
            throw new Exception($"Mercurial operation failed with exit code {proc.ExitCode}:\n{err}");
        }
    }
}