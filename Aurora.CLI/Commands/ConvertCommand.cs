using System.Diagnostics;
using Aurora.Core.Logic.Build;
using Aurora.Core.Parsing;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class ConvertCommand : ICommand
{
    public string Name => "convert";
    public string Description => "Convert an Arch Linux package (.pkg.tar.zst) to Aurora (.au)";

    public async Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("Usage: convert <file.pkg.tar.zst> [output_dir]");
        
        string inputFile = Path.GetFullPath(args[0]);
        string outputDir = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.GetDirectoryName(inputFile)!;

        if (!File.Exists(inputFile)) throw new FileNotFoundException(inputFile);

        string tempDir = Path.Combine(Path.GetTempPath(), $"aurora_convert_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            AnsiConsole.MarkupLine($"[blue]Converting:[/] {Path.GetFileName(inputFile)}");

            // 1. Extract using system tar (handles zstd automatically)
            await AnsiConsole.Status().StartAsync("Extracting ZSTD archive...", async ctx =>
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "tar",
                    Arguments = $"-xf \"{inputFile}\" -C \"{tempDir}\"",
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var proc = Process.Start(psi);
                await proc!.WaitForExitAsync();
                
                if (proc.ExitCode != 0)
                    throw new Exception($"Extraction failed: {await proc.StandardError.ReadToEndAsync()}");
            });

            // 2. Parse .PKGINFO
            string pkgInfoPath = Path.Combine(tempDir, ".PKGINFO");
            if (!File.Exists(pkgInfoPath)) throw new Exception("Invalid package: .PKGINFO not found");

            var manifest = PkgInfoParser.Parse(File.ReadAllText(pkgInfoPath));
            
            // Set source hash to the hash of the input binary package (best effort for provenance)
            manifest.Files.SourceHash = Aurora.Core.Security.HashHelper.ComputeFileHash(inputFile);

            AnsiConsole.MarkupLine($"[green]Identify:[/] {manifest.Package.Name} v{manifest.Package.Version}");

            // 3. Cleanup Arch Metadata
            File.Delete(pkgInfoPath);
            var buildInfo = Path.Combine(tempDir, ".BUILDINFO");
            if (File.Exists(buildInfo)) File.Delete(buildInfo);
            var mtree = Path.Combine(tempDir, ".MTREE");
            if (File.Exists(mtree)) File.Delete(mtree);

            // 4. Create Aurora Artifact
            await ArtifactCreator.CreateAsync(manifest, tempDir, outputDir);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Conversion Failed:[/] {ex.Message}");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}