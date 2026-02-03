using System.Formats.Tar;
using System.IO.Compression;
using Aurora.Core.Contract;
using Aurora.Core.Parsing;
using Spectre.Console;

namespace Aurora.Core.Logic.Build;

public static class ArtifactCreator
{
    public static async Task CreateAsync(AuroraManifest manifest, string pkgDir, string outputDir)
    {
        string fileName = $"{manifest.Package.Name}-{manifest.Package.Version}-{manifest.Package.Architecture}.au";
        string outputPath = Path.Combine(outputDir, fileName);

        AnsiConsole.MarkupLine($"[bold]Compressing artifact:[/] [cyan]{fileName}[/]");

        // 1. Auto-Link (ELF Scanning)
        // This populates manifest.Metadata.Provides with "libreadline.so=8-64"
        await ElfLinker.ProcessArtifactsAsync(manifest, pkgDir);

        // 2. Calculate Size
        manifest.Files.PackageSize = GetDirectorySize(pkgDir);

        // 3. Write .PKGINFO (The standard Arch format)
        var metaContent = ManifestWriter.SerializeToPkgInfo(manifest);
    
        // NOTE: makepkg puts .PKGINFO at the root.
        File.WriteAllText(Path.Combine(pkgDir, ".PKGINFO"), metaContent);

        // 4. Create Archive (Zstd or Gzip)
        if (File.Exists(outputPath)) File.Delete(outputPath);

        // Switch to TarWriter...
        // IMPORTANT: Ensure .PKGINFO is the FIRST entry for speed? 
        // Usually standard tar tools handle it, but it's good practice.
        using (var fs = File.Create(outputPath))
        using (var gz = new GZipStream(fs, CompressionLevel.Optimal)) 
        using (var tar = new TarWriter(gz, TarEntryFormat.Pax))
        {
            await AddDirectoryToTarRecursive(tar, pkgDir, "");
        }

        // Cleanup: We don't delete .PKGINFO from source dir usually, 
        // but in a clean build env the whole pkgDir is wiped anyway.
    
        AnsiConsole.MarkupLine($"[green]✔ Artifact created at {outputPath}[/]");
    }

    private static async Task AddDirectoryToTarRecursive(TarWriter writer, string sourceDir, string entryPrefix)
    {
        var dirInfo = new DirectoryInfo(sourceDir);
        
        // Use standard Unix forward slashes for the prefix
        entryPrefix = entryPrefix.Replace('\\', '/');

        foreach (var item in dirInfo.GetFileSystemInfos())
        {
            var entryName = string.IsNullOrEmpty(entryPrefix) 
                ? item.Name 
                : $"{entryPrefix}/{item.Name}";

            // --- FIX: Handle Symbolic Links ---
            if (item.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                var entry = new PaxTarEntry(TarEntryType.SymbolicLink, entryName);
                
                // .NET 6+ property to get the link target
                entry.LinkName = item.LinkTarget;

                // Handle permissions (Default to 777 for links, but good to try)
                if (OperatingSystem.IsLinux())
                {
                    try { entry.Mode = File.GetUnixFileMode(item.FullName); } catch {}
                }

                await writer.WriteEntryAsync(entry);
                continue; // Done with this item, do NOT recurse or open
            }

            if (item is DirectoryInfo subDir)
            {
                var entry = new PaxTarEntry(TarEntryType.Directory, entryName);
                if (OperatingSystem.IsLinux()) entry.Mode = File.GetUnixFileMode(subDir.FullName);
                
                await writer.WriteEntryAsync(entry);
                await AddDirectoryToTarRecursive(writer, subDir.FullName, entryName);
            }
            else if (item is FileInfo file)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, entryName);
                
                if (OperatingSystem.IsLinux()) entry.Mode = File.GetUnixFileMode(file.FullName);

                // For regular files, we open the stream
                using var stream = File.OpenRead(file.FullName);
                entry.DataStream = stream;
                await writer.WriteEntryAsync(entry);
            }
        }
    }

    private static long GetDirectorySize(string path)
    {
        // Simple size calculation (doesn't account for dedup/sparse but good enough for metadata)
        return Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                        .Select(f => new FileInfo(f).Length)
                        .Sum();
    }
}