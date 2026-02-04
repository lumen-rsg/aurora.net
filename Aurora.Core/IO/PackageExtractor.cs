using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Aurora.Core.Models;
using Aurora.Core.Parsing;

namespace Aurora.Core.IO;

public static class PackageExtractor
{
    public static Package ReadManifest(string packagePath)
    {
        if (!File.Exists(packagePath)) 
            throw new FileNotFoundException($"Package file not found: {packagePath}");

        try
        {
            // 1. Try Fast .NET Path (Only works for .gz)
            return ReadManifestInternal(packagePath);
        }
        catch
        {
            // 2. Fallback to System Tar (Robust Path for .zst)
            return ReadManifestViaTar(packagePath);
        }
    }

    public static List<string> GetFileList(string packagePath)
    {
        try
        {
            return GetFileListInternal(packagePath);
        }
        catch
        {
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

            if (name == ".PKGINFO")
            {
                using var stream = entry.DataStream;
                if (stream == null) throw new InvalidDataException("Manifest is empty");

                using var reader = new StreamReader(stream);
                var content = reader.ReadToEnd();
                return PackageParser.ParsePkgInfo(content);
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

            if (string.IsNullOrEmpty(name) || name == ".PKGINFO" || name == ".INSTALL" || name == ".MTREE" || name.EndsWith("/"))
                continue;

            files.Add("/" + name.TrimStart('/'));
        }
        return files;
    }

    // --- SYSTEM TAR FALLBACKS (FIXED) ---

    private static Package ReadManifestViaTar(string packagePath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "tar",
            // -x: extract, -O: to stdout, -f: file
            Arguments = $"-xO -f \"{packagePath}\" .PKGINFO",
            RedirectStandardOutput = true,
            RedirectStandardError = true, // We capture this to prevent deadlock
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) throw new Exception("Failed to start tar.");

        // FIX: Use events to read streams asynchronously to avoid deadlocks
        // if the buffer fills up.
        StringBuilder stdout = new();
        StringBuilder stderr = new();

        process.OutputDataReceived += (sender, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (sender, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        process.WaitForExit();

        if (process.ExitCode != 0 || stdout.Length == 0)
        {
            // If tar failed, we might want to see why
            string err = stderr.ToString();
            throw new Exception($"Failed to extract .PKGINFO from {Path.GetFileName(packagePath)}. Tar error: {err}");
        }

        return PackageParser.ParsePkgInfo(stdout.ToString());
    }

    private static List<string> GetFileListViaTar(string packagePath)
    {
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

        // Synchronous read is safer here if we don't care about stderr blocking 
        // (usually file lists don't generate massive stderr warnings), 
        // but for consistency, let's use the event model if the list is huge.
        // However, for lists, line-by-line processing is often cleaner. 
        // We will stick to standard readline loop but read stderr at the end.
        
        string? line;
        while ((line = process.StandardOutput.ReadLine()) != null)
        {
            var name = line.Trim().Replace('\\', '/');
            if (name.StartsWith("./")) name = name.Substring(2);

            var fileName = Path.GetFileName(name);
            
            if (string.IsNullOrEmpty(fileName) || 
                name.EndsWith("/") ||
                name.Contains(".AURORA_") || 
                name == ".PKGINFO" || 
                name == ".MTREE" || 
                name == ".BUILDINFO" ||
                name == ".INSTALL")
                continue;

            files.Add("/" + name.TrimStart('/'));
        }
        
        // Drain stderr to ensure process closes
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        
        return files;
    }
}