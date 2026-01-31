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

        // 1. Calculate installed size
        manifest.Files.PackageSize = GetDirectorySize(pkgDir);

        // 2. Write metadata into the pkgDir so it gets included in the tar
        // But we use a standard name inside the archive
        var metaContent = ManifestWriter.Serialize(manifest);
        File.WriteAllText(Path.Combine(pkgDir, "aurora.meta"), metaContent);

        // 3. Create Gzipped Tar
        if (File.Exists(outputPath)) File.Delete(outputPath);

        using (var fs = File.Create(outputPath))
        using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
        using (var tar = new TarWriter(gz, TarEntryFormat.Pax))
        {
            // Recursively add files from pkgDir
            await AddDirectoryToTarRecursive(tar, pkgDir, "");
        }
        
        var hash = Aurora.Core.Security.HashHelper.ComputeFileHash(outputPath);
        manifest.Files.SourceHash = hash;

        AnsiConsole.MarkupLine($"[green]✔ Artifact created at {outputPath}[/]");
    }

    private static async Task AddDirectoryToTarRecursive(TarWriter writer, string sourceDir, string entryPrefix)
    {
        var dirInfo = new DirectoryInfo(sourceDir);
        foreach (var item in dirInfo.GetFileSystemInfos())
        {
            // Ensure forward slashes for Linux compatibility
            var entryName = string.IsNullOrEmpty(entryPrefix) 
                ? item.Name 
                : $"{entryPrefix}/{item.Name}";

            if (item is DirectoryInfo subDir)
            {
                var entry = new PaxTarEntry(TarEntryType.Directory, entryName);
                await writer.WriteEntryAsync(entry);
                await AddDirectoryToTarRecursive(writer, subDir.FullName, entryName);
            }
            else if (item is FileInfo file)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, entryName);
                // Set the correct mode (755 for scripts/bins, 644 for others)
                // For a simple build tool, we can preserve host attributes if on Linux
                if (OperatingSystem.IsLinux()) {
                    entry.Mode = File.GetUnixFileMode(file.FullName);
                }

                using var stream = File.OpenRead(file.FullName);
                entry.DataStream = stream;
                await writer.WriteEntryAsync(entry);
            }
        }
    }

    private static long GetDirectorySize(string path)
    {
        return Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                        .Select(f => new FileInfo(f).Length)
                        .Sum();
    }
}