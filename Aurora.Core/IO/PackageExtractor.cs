using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using Aurora.Core.Models;
using Aurora.Core.Parsing;
using Spectre.Console;

namespace Aurora.Core.IO;

public static class PackageExtractor
{
    public static Package ReadManifest(string packagePath)
    {
        if (!File.Exists(packagePath)) 
            throw new FileNotFoundException($"Package file not found: {packagePath}");

        try
        {
            // 1. Try Fast .NET Path
            return ReadManifestInternal(packagePath);
        }
        catch
        {
            // 2. Fallback to System Tar (Robust Path)
            // au-repotool needs to be bulletproof. If .NET fails on a specific gzip flag,
            // the system tar usually handles it fine.
            return ReadManifestViaTar(packagePath);
        }
    }

    public static List<string> GetFileList(string packagePath)
    {
        try
        {
            // 1. Try Fast .NET Path
            return GetFileListInternal(packagePath);
        }
        catch
        {
            // 2. Fallback to System Tar
            return GetFileListViaTar(packagePath);
        }
    }

    // --- INTERNAL .NET IMPLEMENTATIONS ---

    private static Package ReadManifestInternal(string packagePath)
    {
        using var fs = File.OpenRead(packagePath);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var tar = new TarReader(gz);

        while (tar.GetNextEntry() is { } entry)
        {
            var name = entry.Name.Replace('\\', '/');
            if (name.StartsWith("./")) name = name.Substring(2);

            if (name == "aurora.meta" || name == ".AURORA_META")
            {
                using var stream = entry.DataStream;
                if (stream == null) throw new InvalidDataException("Manifest is empty");

                using var reader = new StreamReader(stream);
                var content = reader.ReadToEnd();
                return PackageParser.ParseManifest(content);
            }
        }
        throw new InvalidDataException("Manifest not found via .NET reader");
    }

    private static List<string> GetFileListInternal(string packagePath)
    {
        var files = new List<string>();
        using var fs = File.OpenRead(packagePath);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var tar = new TarReader(gz);

        while (tar.GetNextEntry() is { } entry)
        {
            var name = entry.Name.Replace('\\', '/');
            if (name.StartsWith("./")) name = name.Substring(2);

            var fileName = Path.GetFileName(name);
            if (string.IsNullOrEmpty(fileName) || 
                name.Contains(".AURORA_") || 
                name == "aurora.meta" || 
                name == ".INSTALL")
                continue;

            files.Add("/" + name.TrimStart('/'));
        }
        return files;
    }

    // --- SYSTEM TAR FALLBACKS ---

    private static Package ReadManifestViaTar(string packagePath)
    {
        // Command: tar -xO -f package.au aurora.meta
        // -x: extract, -O: to stdout, -f: file
        
        var psi = new ProcessStartInfo
        {
            FileName = "tar",
            Arguments = $"-xO -f \"{packagePath}\" aurora.meta",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) throw new Exception("Failed to start tar.");

        var content = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(content))
        {
            // Try legacy name
            psi.Arguments = $"-xO -f \"{packagePath}\" .AURORA_META";
            using var legacyProc = Process.Start(psi);
            content = legacyProc?.StandardOutput.ReadToEnd() ?? "";
            legacyProc?.WaitForExit();
            
            if (string.IsNullOrWhiteSpace(content))
                throw new Exception($"Failed to extract manifest from {Path.GetFileName(packagePath)} using system tar.");
        }

        return PackageParser.ParseManifest(content);
    }

    private static List<string> GetFileListViaTar(string packagePath)
    {
        // Command: tar -tf package.au
        // -t: list
        var files = new List<string>();
        var psi = new ProcessStartInfo
        {
            FileName = "tar",
            Arguments = $"-tf \"{packagePath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) return files;

        string? line;
        while ((line = process.StandardOutput.ReadLine()) != null)
        {
            var name = line.Trim().Replace('\\', '/');
            if (name.StartsWith("./")) name = name.Substring(2);

            var fileName = Path.GetFileName(name);
            
            // Filter metadata and directories ending in /
            if (string.IsNullOrEmpty(fileName) || 
                name.EndsWith("/") ||
                name.Contains(".AURORA_") || 
                name == "aurora.meta" || 
                name == ".INSTALL")
                continue;

            files.Add("/" + name.TrimStart('/'));
        }
        
        process.WaitForExit();
        return files;
    }
}