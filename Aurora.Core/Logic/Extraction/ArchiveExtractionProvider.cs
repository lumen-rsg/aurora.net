using System.Diagnostics;
using System.IO.Compression;
using Aurora.Core.Models;

namespace Aurora.Core.Logic.Extraction;

public class ArchiveExtractionProvider : IExtractionProvider
{
    public bool CanHandle(SourceEntry entry) => entry.Protocol == "local" || entry.Protocol == "http" || entry.Protocol == "https";

    public async Task ExtractAsync(SourceEntry entry, string sourcePath, string srcDir, Action<string> onProgress)
    {
        var targetLink = Path.Combine(srcDir, entry.FileName);
        
        if (File.Exists(targetLink) || Directory.Exists(targetLink)) File.Delete(targetLink);
        File.CreateSymbolicLink(targetLink, sourcePath);

        if (IsArchive(entry.FileName))
        {
            onProgress($"Extracting {entry.FileName}...");
            
            // Use system 'tar' for robustness, as it handles all compression types.
            // -x: extract, -f: from file, -C: change to directory
            var psi = new ProcessStartInfo("tar", $"-xf \"{targetLink}\" -C \"{srcDir}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null) throw new Exception("Failed to start 'tar' process.");
            
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                // tar can exit non-zero for warnings, only throw on fatal errors
                if (error.Contains("Error", StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception($"Tar extraction failed for {entry.FileName}: {error}");
                }
            }
        }
    }

    private bool IsArchive(string filename)
    {
        var f = filename.ToLowerInvariant();
        // Check for common tar formats first
        if (f.Contains(".tar") || f.EndsWith(".tgz")) return true;

        // Check for single-file compression formats
        var ext = Path.GetExtension(f);
        return ext switch
        {
            ".gz" => true,
            ".bz2" => true,
            ".xz" => true,
            ".lz" => true,
            ".lz4" => true,
            ".zst" => true,
            ".zip" => true,
            _ => false
        };
    }
}