using System.Diagnostics;
using Aurora.Core.Logging;

namespace Aurora.Core.IO;

public static class PackageInstaller
{
    public static void InstallPackage(
        string packagePath, 
        string rootFsPath, 
        Action<string, string>? onFileExtracted, 
        bool stagingMode = false)
    {
        AuLogger.Info($"Installing {packagePath} to {rootFsPath}...");

        if (!File.Exists(packagePath)) throw new FileNotFoundException(packagePath);

        // 1. Get the file list pre-emptively for the database journal
        // This uses our existing 'tar -tf' logic which handles zstd.
        var fileList = PackageExtractor.GetFileList(packagePath);

        // 2. Determine target path
        // If stagingMode is true (used by system updates), we extract to a temp subdir
        // and then the SystemUpdater handles the atomic move.
        string extractionTarget = rootFsPath;
        if (stagingMode)
        {
            extractionTarget = Path.Combine(rootFsPath, ".aurora_staging_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(extractionTarget);
        }

        // 3. Execute System Tar
        // -x: extract
        // -p: preserve permissions (crucial)
        // --numeric-owner: Don't map UIDs to host names (crucial for bootstrap/container builds)
        // --auto-compress: Detect .gz or .zst automatically
        var psi = new ProcessStartInfo
        {
            FileName = "tar",
            Arguments = $"-xpf \"{packagePath}\" --numeric-owner -C \"{extractionTarget}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi);
        if (process == null) throw new Exception("Failed to start system tar for extraction.");

        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new Exception($"Extraction failed for {Path.GetFileName(packagePath)}: {stderr}");
        }

        // 4. Update the Journal/Database
        // We iterate the list we got from 'tar -tf' earlier.
        foreach (var file in fileList)
        {
            var physicalPath = PathHelper.GetPath(extractionTarget, file);
            
            // If we are in staging mode, the actual physical path is inside the temp folder.
            // But the manifest path (what goes in the DB) is always the absolute system path.
            onFileExtracted?.Invoke(physicalPath, file);
        }
    }

    public static string? ExtractScript(string packagePath, string outputDir)
    {
        // Extract .INSTALL or .AURORA_SCRIPTS via system tar
        var scriptNames = new[] { ".INSTALL", ".AURORA_SCRIPTS" };
        
        foreach (var name in scriptNames)
        {
            try 
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "tar",
                    Arguments = $"-xOf \"{packagePath}\" \"{name}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                string content = proc?.StandardOutput.ReadToEnd() ?? "";
                proc?.WaitForExit();

                if (proc?.ExitCode == 0 && !string.IsNullOrWhiteSpace(content))
                {
                    var dest = Path.Combine(outputDir, $".INSTALL_{Path.GetFileNameWithoutExtension(packagePath)}");
                    File.WriteAllText(dest, content);
                    return dest;
                }
            } catch { /* Try next name */ }
        }
        return null;
    }
}