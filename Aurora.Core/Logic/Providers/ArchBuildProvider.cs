using Aurora.Core.Contract;
using Aurora.Core.Logic.Build;
using Aurora.Core.Logic.Extraction;
using Aurora.Core.Models;
using Aurora.Core.Security;
using Spectre.Console;

namespace Aurora.Core.Logic.Providers;

public class ArchBuildProvider : IBuildProvider
{
    public string FormatName => "Arch Linux (PKGBUILD)";

    public bool CanHandle(string directory)
    {
        return File.Exists(Path.Combine(directory, "PKGBUILD"));
    }

    public async Task<AuroraManifest> GetManifestAsync(string directory)
    {
        var engine = new ArchBuildEngine(directory);
        var pkgbuildPath = Path.Combine(directory, "PKGBUILD");
        
        var manifest = await engine.InspectPkgbuildAsync(pkgbuildPath);
        if (manifest == null) throw new Exception("Failed to extract metadata from PKGBUILD");
        
        return manifest;
    }

    public async Task FetchSourcesAsync(AuroraManifest manifest, string downloadDir, bool skipGpg, string startDir) // Added startDir
    {
        if (!Directory.Exists(downloadDir)) Directory.CreateDirectory(downloadDir);

        // Pass startDir to SourceManager
        var sourceMgr = new SourceManager(startDir);
        
        AnsiConsole.MarkupLine("[bold]Fetching sources...[/]");

        foreach (var sourceStr in manifest.Build.Source)
        {
            var entry = new SourceEntry(sourceStr);
            // downloadDir (SRCDEST) is the target destination for remote stuff
            await sourceMgr.FetchSourceAsync(entry, downloadDir, msg => 
            {
                AnsiConsole.MarkupLine($"  [grey]-> {msg}[/]");
            });
        }
        
        // 2. Verify Checksums
        var integrity = new IntegrityManager();
        integrity.VerifyChecksums(manifest, downloadDir, startDir);

        // 3. Verify Signatures (Only if NOT skipped)
        if (!skipGpg)
        {
            var sigVerifier = new SignatureVerifier();
            sigVerifier.VerifySignatures(manifest, downloadDir, startDir);
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]! Skipping GPG signature verification.[/]");
        }
    }

    public async Task BuildAsync(AuroraManifest manifest, string buildDir, string startDir, Action<string> logAction)
    {
        DependencyStub.CheckBuildDependencies(manifest.Dependencies.Build);

        var absoluteBuildDir = Path.GetFullPath(buildDir);
        var absoluteStartDir = Path.GetFullPath(startDir);

        var exec = new ExecutionManager(absoluteBuildDir, absoluteStartDir, manifest);
        var extractor = new SourceExtractor();
        
        exec.PrepareDirectories();

        // Extract sources, passing startDir so it can find local files
        var cacheDir = Path.Combine(absoluteStartDir, "SRCDEST"); 
        var srcDir = Path.Combine(absoluteBuildDir, "src");
        Directory.CreateDirectory(srcDir);

        await extractor.ExtractAllAsync(manifest, cacheDir, srcDir, absoluteStartDir);

        // Pass the logAction to the execution manager
        await exec.RunBuildFunctionAsync("prepare", logAction);
        await exec.RunBuildFunctionAsync("build", logAction);
        
        if (manifest.Build.Environment.Contains("check"))
            await exec.RunBuildFunctionAsync("check", logAction);

        // 2. Run fakeroot-space functions and create artifacts
        var packageNames = manifest.Package.AllNames.Any() 
            ? manifest.Package.AllNames 
            : new List<string> { manifest.Package.Name };

        foreach (var name in packageNames)
        {
            AnsiConsole.MarkupLine($"[bold magenta]Packaging Phase:[/] [white]{name}[/]");
            
            string funcName = packageNames.Count > 1 || name != manifest.Package.Name 
                ? $"package_{name}" 
                : "package";

            // Use the fakeroot-enabled method
            var subManifest = await exec.RunPackageFunctionAsync(funcName, manifest, logAction);

            var subPkgDir = Path.Combine(absoluteBuildDir, "pkg", name);
            await ArtifactCreator.CreateAsync(subManifest, subPkgDir, absoluteStartDir);
        }

        AnsiConsole.MarkupLine("[green bold]Build process completed successfully![/]");
    }
}