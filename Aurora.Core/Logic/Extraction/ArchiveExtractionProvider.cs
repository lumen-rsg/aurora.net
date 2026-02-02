using System.Diagnostics;
using Aurora.Core.Models;
using Spectre.Console;

namespace Aurora.Core.Logic.Extraction;

public class ArchiveExtractionProvider : IExtractionProvider
{
    public bool CanHandle(SourceEntry entry)
    {
        return IsArchive(entry.FileName);
    }

    public async Task ExtractAsync(SourceEntry entry, string sourcePath, string srcDir, Action<string> onProgress)
    {
        var targetLink = Path.Combine(srcDir, entry.FileName);
        
        // 1. Symlink source into srcdir
        if (File.Exists(targetLink) || Directory.Exists(targetLink)) File.Delete(targetLink);
        File.CreateSymbolicLink(targetLink, sourcePath);

        onProgress($"Extracting {entry.FileName}...");

        ProcessStartInfo psi;

        // 2. Branch Logic: Zip vs Tar
        if (entry.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            // unzip -o (overwrite) -q (quiet) source -d (destination)
            psi = new ProcessStartInfo("unzip", $"-o -q \"{targetLink}\" -d \"{srcDir}\"");
        }
        else
        {
            // tar -x (extract) -f (file) -C (change dir)
            psi = new ProcessStartInfo("tar", $"-xf \"{targetLink}\" -C \"{srcDir}\"");
        }

        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardError = true;

        // 3. Execute
        using var process = Process.Start(psi);
        if (process == null) throw new Exception($"Failed to start extraction process for {entry.FileName}");
        
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            // Handle common non-fatal warnings vs real errors
            if (error.Contains("Error", StringComparison.OrdinalIgnoreCase) || 
                error.Contains("cannot find", StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception($"Extraction failed for {entry.FileName}: {error}");
            }
        }
    }

    private bool IsArchive(string filename)
    {
        var f = filename.ToLowerInvariant();
        if (f.EndsWith(".sig") || f.EndsWith(".asc")) return false;

        if (f.Contains(".tar") || f.EndsWith(".tgz") || f.EndsWith(".tbz2")) return true;

        var ext = Path.GetExtension(f);
        return ext switch
        {
            ".gz" => true,
            ".bz2" => true,
            ".xz" => true,
            ".lz" => true,
            ".lz4" => true,
            ".zst" => true,
            ".zip" => true, // Ensure zip is recognized
            _ => false
        };
    }
}