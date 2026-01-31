using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Aurora.Core.Logging;
using Spectre.Console;

namespace Aurora.Core.IO;

public static class PackageInstaller
{
    
    private static readonly HashSet<string> _ignoredFiles = new()
    {
        "aurora.meta",
        ".AURORA_META",
        ".INSTALL",
        ".AURORA_SCRIPTS",
        ".MTREE",
        ".PKGINFO"
    };
    
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
            var name = entry.Name.Replace('\\', '/');
            if (name.StartsWith("./")) name = name.Substring(2);

            // --- FIX: Support both .INSTALL and legacy .AURORA_SCRIPTS ---
            if (name == ".INSTALL" || name == ".AURORA_SCRIPTS")
            {
                var dest = Path.Combine(outputDir, $".INSTALL_{Path.GetFileNameWithoutExtension(packagePath)}");
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
        if (entryName.StartsWith("./")) entryName = entryName.Substring(2);

        // --- FIX: Filter out Metadata Files ---
        // We check the filename part to catch these files at the root of the archive
        var fileName = Path.GetFileName(entryName);
        if (_ignoredFiles.Contains(fileName)) 
        {
            // AuLogger.Debug($"Skipping metadata file: {fileName}");
            return;
        }

        var physicalPath = PathHelper.GetPath(rootPath, entryName);
        var manifestPath = "/" + entryName.TrimStart('/');
        var targetPath = stagingMode ? physicalPath + ".aurora_new" : physicalPath;

        if (!physicalPath.StartsWith(Path.GetFullPath(rootPath)))
             throw new IOException($"Zip Slip detected: {entryName}");

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
    
    public static Aurora.Core.Models.Package ReadManifestFromPackage(string packagePath)
    {
        using var fs = File.OpenRead(packagePath);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var tar = new TarReader(gz);

        while (tar.GetNextEntry() is { } entry)
        {
            var name = entry.Name.Replace('\\', '/');
            if (name.StartsWith("./")) name = name.Substring(2);

            // Check for new Contract OR Legacy format
            if (name == "aurora.meta" || name == ".AURORA_META")
            {
                using var stream = entry.DataStream;
                if (stream == null) throw new InvalidDataException("Manifest entry is empty.");
                
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var content = reader.ReadToEnd();
                
                // Use our Parser
                return Aurora.Core.Parsing.PackageParser.ParseManifest(content);
            }
        }

        throw new InvalidDataException("No metadata file (aurora.meta) found in package.");
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