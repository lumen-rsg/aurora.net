using System.Diagnostics;
using Aurora.Core.Models;

namespace Aurora.Core.Logic.Extraction;

public class ArchiveExtractionProvider : IExtractionProvider
{
    // FIX: This provider ONLY handles files that are archives.
    public bool CanHandle(SourceEntry entry)
    {
        return IsArchive(entry.FileName);
    }

    public async Task ExtractAsync(SourceEntry entry, string sourcePath, string srcDir, Action<string> onProgress)
    {
        var targetLink = Path.Combine(srcDir, entry.FileName);
        
        // Always symlink the source into $srcdir first.
        if (File.Exists(targetLink) || Directory.Exists(targetLink)) File.Delete(targetLink);
        File.CreateSymbolicLink(targetLink, sourcePath);

        onProgress($"Extracting {entry.FileName}...");

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

        if (process.ExitCode != 0 && error.Contains("Error", StringComparison.OrdinalIgnoreCase))
        {
            throw new Exception($"Tar extraction failed for {entry.FileName}: {error}");
        }
    }

    private bool IsArchive(string filename)
    {
        var f = filename.ToLowerInvariant();
        
        // This is not an archive, it's a signature
        if (f.EndsWith(".sig") || f.EndsWith(".asc")) return false;

        // Check for common tar formats first
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
            ".zip" => true,
            _ => false
        };
    }
}