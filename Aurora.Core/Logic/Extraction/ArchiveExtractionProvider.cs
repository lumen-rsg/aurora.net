using System.Diagnostics;
using Aurora.Core.Models;

namespace Aurora.Core.Logic.Extraction;

public class ArchiveExtractionProvider : IExtractionProvider
{
    public bool CanHandle(SourceEntry entry) => IsArchive(entry.FileName);

    public async Task ExtractAsync(SourceEntry entry, string sourcePath, string srcDir, Action<string> onProgress)
    {
        onProgress($"Extracting {entry.FileName}...");

        ProcessStartInfo psi;
        if (entry.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            psi = new ProcessStartInfo("unzip", $"-o -q \"{sourcePath}\" -d \"{srcDir}\"");
        }
        else
        {
            // Extract from the absolute sourcePath in SRCDEST directly into srcDir
            psi = new ProcessStartInfo("tar", $"-xf \"{sourcePath}\" -C \"{srcDir}\"");
        }

        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardError = true;

        using var process = Process.Start(psi);
        if (process == null) throw new Exception($"Failed to start extraction for {entry.FileName}");
        
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            if (!error.Contains("warning", StringComparison.OrdinalIgnoreCase))
                throw new Exception($"Extraction failed for {entry.FileName}: {error}");
        }
    }

    private bool IsArchive(string filename)
    {
        var f = filename.ToLowerInvariant();
        if (f.EndsWith(".sig") || f.EndsWith(".asc") || f.EndsWith(".sign")) return false;
        if (f.Contains(".tar") || f.EndsWith(".tgz") || f.EndsWith(".tbz2") || f.EndsWith(".txz") || f.EndsWith(".zip")) return true;

        var ext = Path.GetExtension(f);
        return ext switch { ".gz" => true, ".bz2" => true, ".xz" => true, "lz4" => true, ".zst" => true, _ => false };
    }
}