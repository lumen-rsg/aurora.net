using Aurora.Core.Models;
using System.Diagnostics;

namespace Aurora.Core.Logic.Extraction;

public class VcsExtractionProvider : IExtractionProvider
{
    public bool CanHandle(SourceEntry entry) => entry.Protocol == "git" || entry.Protocol == "svn";

    public async Task ExtractAsync(SourceEntry entry, string cachePath, string srcDir, Action<string> onProgress)
    {
        if (entry.Protocol == "git")
        {
            // The directory where the code should actually live (e.g. .aurora-build/src/acl)
            var destination = Path.Combine(srcDir, entry.FileName);
            
            if (Directory.Exists(destination)) Directory.Delete(destination, true);

            onProgress($"Extracting {entry.FileName} from mirror...");

            // 1. Clone from the LOCAL bare mirror to create a working tree
            // We use the absolute path of the cachePath to ensure git finds it
            var absoluteCache = Path.GetFullPath(cachePath);
            await RunGitCommandAsync($"clone \"{absoluteCache}\" \"{destination}\"");

            // 2. Checkout the specific version if defined (fragment #tag=v2.3.2)
            string target = "HEAD";
            if (!string.IsNullOrEmpty(entry.Fragment))
            {
                target = entry.Fragment.Contains('=') ? entry.Fragment.Split('=')[1] : entry.Fragment;
                onProgress($"Checking out {target}...");
                await RunGitCommandAsync($"checkout {target}", destination);
            }
            
            // 3. Make sure submodules are handled (Arch standard)
            await RunGitCommandAsync($"submodule update --init --recursive", destination);
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