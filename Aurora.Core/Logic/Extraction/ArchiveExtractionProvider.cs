using System.Diagnostics;
using Aurora.Core.Models;

namespace Aurora.Core.Logic.Extraction;

public class ArchiveExtractionProvider : IExtractionProvider
{
    public bool CanHandle(SourceEntry entry) => 
        entry.Protocol == "local" || entry.Protocol == "http" || entry.Protocol == "https";

    public async Task ExtractAsync(SourceEntry entry, string cachePath, string srcDir, Action<string> onProgress)
    {
        var targetLink = Path.Combine(srcDir, entry.FileName);
        
        // 1. Symlink the source into $srcdir (Standard makepkg behavior)
        if (File.Exists(targetLink) || Directory.Exists(targetLink)) 
            File.Delete(targetLink);
            
        File.CreateSymbolicLink(targetLink, cachePath);

        // 2. Identify if it's an extractable archive
        if (IsArchive(entry.FileName))
        {
            onProgress($"Extracting {entry.FileName}...");

            var psi = new ProcessStartInfo
            {
                FileName = "tar",
                // -x: extract, -f: file, -C: destination directory
                // --no-same-owner: usually safer for user-space builds
                Arguments = $"-xf \"{targetLink}\" -C \"{srcDir}\" --no-same-owner",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                // Note: We don't throw for all errors because some tars have 
                // "directory changed underfoot" warnings that return non-zero but worked.
                // But for "No such file", it's a real failure.
                if (error.Contains("Error")) throw new Exception($"Tar extraction failed: {error}");
            }
        }
    }

    private bool IsArchive(string filename)
    {
        var f = filename.ToLower();
        return f.EndsWith(".tar") || f.EndsWith(".tar.gz") || f.EndsWith(".tgz") || 
               f.EndsWith(".tar.bz2") || f.EndsWith(".tbz2") || f.EndsWith(".tar.xz") || 
               f.EndsWith(".tar.zst") || f.EndsWith(".zip");
    }
}