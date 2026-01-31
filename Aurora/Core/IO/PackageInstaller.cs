using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Aurora.Core.Logging;
using Spectre.Console;

namespace Aurora.Core.IO;

public static class PackageInstaller
{
    // UPDATE: Callback now takes (PhysicalPath, ManifestPath)
    public static void InstallPackage(
        string packagePath, 
        string rootFsPath, 
        Action<string, string>? onFileExtracted, 
        bool stagingMode = false)
    {
        AuLogger.Info($"Installing {packagePath} to {rootFsPath}...");

        if (!File.Exists(packagePath)) throw new FileNotFoundException(packagePath);

        using var fs = File.OpenRead(packagePath);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var tar = new TarReader(gz);

        while (tar.GetNextEntry() is { } entry)
        {
            ExtractEntry(entry, rootFsPath, onFileExtracted, stagingMode);
        }
    }
    
    // Add this static method
    public static string? ExtractScript(string packagePath, string outputDir)
    {
        using var fs = File.OpenRead(packagePath);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var tar = new TarReader(gz);

        while (tar.GetNextEntry() is { } entry)
        {
            // FIX: Handle leading ./
            var name = entry.Name;
            if (name.StartsWith("./")) name = name.Substring(2);

            if (name == ".AURORA_SCRIPTS")
            {
                // Use a unique name so parallel installs don't clash (if we ever did that)
                // or just to avoid stale files
                var dest = Path.Combine(outputDir, $".AURORA_SCRIPTS_{Path.GetFileNameWithoutExtension(packagePath)}");
                entry.ExtractToFile(dest, overwrite: true);
                return dest;
            }
        }
        return null;
    }

    private static void ExtractEntry(
        TarEntry entry, 
        string rootPath, 
        Action<string, string>? onFileExtracted, 
        bool stagingMode)
    {
        var entryName = entry.Name.Replace('\\', '/');
        
        // Remove ./ prefix if present
        if (entryName.StartsWith("./")) entryName = entryName.Substring(2);

        // 1. Calculate Physical Path
        var physicalPath = PathHelper.GetPath(rootPath, entryName);
        
        // 2. Calculate Manifest Path
        var manifestPath = "/" + entryName.TrimStart('/');

        // 3. Staging Logic
        var targetPath = stagingMode ? physicalPath + ".aurora_new" : physicalPath;

        // Security Check
        if (!physicalPath.StartsWith(Path.GetFullPath(rootPath)))
             throw new IOException($"Zip Slip detected: {entryName}");

        if (Path.GetFileName(physicalPath).StartsWith(".AURORA_")) return;
        
        // AnsiConsole.MarkupLine($"[grey]Extracting: {entryName} -> {targetPath}[/]");

        switch (entry.EntryType)
        {
            case TarEntryType.Directory:
                Directory.CreateDirectory(physicalPath); 
                ApplyMetadata(entry, physicalPath);
                break;

            case TarEntryType.RegularFile:
            case TarEntryType.V7RegularFile:
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                entry.ExtractToFile(targetPath, overwrite: true);
                
                onFileExtracted?.Invoke(physicalPath, manifestPath);
                ApplyMetadata(entry, targetPath);
                break;

            case TarEntryType.SymbolicLink:
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                if (File.Exists(targetPath)) File.Delete(targetPath);
                File.CreateSymbolicLink(targetPath, entry.LinkName);
                
                onFileExtracted?.Invoke(physicalPath, manifestPath);
                ApplyMetadata(entry, targetPath, isSymlink: true);
                break;
        }
    }

    private static void ApplyMetadata(TarEntry entry, string path, bool isSymlink = false)
    {
        // 1. Permissions
        // Symlink permissions on Linux are effectively ignored (always 777), so we skip chmod for them.
        if (!isSymlink && entry.Format != TarEntryFormat.V7)
        {
            try 
            {
                File.SetUnixFileMode(path, entry.Mode);
            }
            catch (Exception ex)
            {
                AuLogger.Debug($"Failed to set mode on {path}: {ex.Message}");
            }
        }

        // --- LINUX ONLY SECTION ---
        // We guard this block so development on macOS/Windows doesn't crash due to missing symbols.
        if (OperatingSystem.IsLinux())
        {
            // 2. Ownership (chown)
            try 
            {
                // 'lchown' works on symlinks themselves (does not follow target)
                Syscall.lchown(path, (uint)entry.Uid, (uint)entry.Gid);
            }
            catch 
            {
                // Usually fails if not Root. Log only in verbose debug.
            }

            // 3. Extended Attributes (xattrs)
            // Only 'PaxTarEntry' supports arbitrary extended attributes.
            if (entry is PaxTarEntry paxEntry && paxEntry.ExtendedAttributes != null)
            {
                foreach (var attribute in paxEntry.ExtendedAttributes)
                {
                    // "SCHILY.xattr." is the standard prefix for xattrs in PAX tars (used by GNU tar)
                    if (attribute.Key.StartsWith("SCHILY.xattr."))
                    {
                        var attrName = attribute.Key.Substring("SCHILY.xattr.".Length);
                        
                        // Explicit string cast to avoid compiler ambiguity
                        string valStr = attribute.Value; 
                        var bytes = Encoding.UTF8.GetBytes(valStr);

                        // 'lsetxattr' sets attribute on the file/link itself
                        // 0 = XATTR_CREATE (fails if exists) or XATTR_REPLACE. 
                        // Actually standard usage allows 0 to mean "create or replace".
                        var res = Syscall.lsetxattr(path, attrName, bytes, (ulong)bytes.Length, 0);
                        
                        if (res != 0)
                        {
                            // Could fail due to FS support or permissions
                            AuLogger.Debug($"Failed to set xattr {attrName} on {path}");
                        }
                    }
                }
            }
        }
    }
}