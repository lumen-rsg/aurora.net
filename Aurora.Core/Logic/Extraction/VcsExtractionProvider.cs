using Aurora.Core.Models;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace Aurora.Core.Logic.Extraction;

public class VcsExtractionProvider : IExtractionProvider
{
    public bool CanHandle(SourceEntry entry) => entry.Protocol == "git" || entry.Protocol == "svn";

    public async Task ExtractAsync(SourceEntry entry, string cachePath, string srcDir, Action<string> onProgress)
    {
        if (entry.Protocol == "git")
        {
            var destination = Path.Combine(srcDir, entry.FileName);
            
            if (Directory.Exists(destination)) Directory.Delete(destination, true);

            onProgress($"Extracting {entry.FileName} from mirror...");

            var absoluteCache = Path.GetFullPath(cachePath);
            await RunGitCommandAsync($"clone \"{absoluteCache}\" \"{destination}\"");

            string target = "HEAD";
            if (!string.IsNullOrEmpty(entry.Fragment))
            {
                target = entry.Fragment.Contains('=') ? entry.Fragment.Split('=')[1] : entry.Fragment;
                onProgress($"Checking out {target}...");
                await RunGitCommandAsync($"checkout {target}", destination);
            }
            
            // --- FIX: Rewrite Submodule URLs before updating ---
            await FixSubmoduleUrls(entry, destination);

            await RunGitCommandAsync($"submodule update --init --recursive", destination);
        }
    }
    
    private async Task FixSubmoduleUrls(SourceEntry parentEntry, string repoPath)
    {
        var gitmodulesPath = Path.Combine(repoPath, ".gitmodules");
        if (!File.Exists(gitmodulesPath)) return;

        string currentSubmoduleName = "";
        foreach (var line in await File.ReadAllLinesAsync(gitmodulesPath))
        {
            var trimmed = line.Trim();

            var match = Regex.Match(trimmed, @"\[submodule ""(.*?)""\]");
            if (match.Success)
            {
                currentSubmoduleName = match.Groups[1].Value;
                continue;
            }

            if (!string.IsNullOrEmpty(currentSubmoduleName) && trimmed.StartsWith("url ="))
            {
                var url = trimmed.Split('=', 2)[1].Trim();

                if (url.StartsWith("./") || url.StartsWith("../") || url.StartsWith("/"))
                {
                    // --- FIX: Clean the parent URL before using it as a base ---
                    var parentUrl = parentEntry.Url;
                    if (parentUrl.StartsWith("git+"))
                    {
                        parentUrl = parentUrl.Substring(4);
                    }
                    var parentUri = new Uri(parentUrl.Split('?')[0]);
                    
                    var absoluteSubmoduleUri = new Uri(parentUri, url);

                    AnsiConsole.MarkupLine($"  [grey]-> Rewriting submodule '{currentSubmoduleName}' URL to {absoluteSubmoduleUri.AbsoluteUri}[/]");
                    
                    await RunGitCommandAsync($"config submodule.{currentSubmoduleName}.url \"{absoluteSubmoduleUri.AbsoluteUri}\"", repoPath);
                }

                currentSubmoduleName = ""; 
            }
        }
    }

    // FIX: Make workingDir optional (string? workingDir = null)
    private async Task RunGitCommandAsync(string arguments, string? workingDir = null)
    {
        var psi = new ProcessStartInfo {
            FileName = "git",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };

        // Only set the working directory if one is provided
        if (!string.IsNullOrEmpty(workingDir))
        {
            psi.WorkingDirectory = workingDir;
        }

        using var proc = Process.Start(psi);
        if (proc == null) throw new Exception("Failed to start git process.");

        var err = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0) 
            throw new Exception($"Git operation failed: {err}");
    }
}