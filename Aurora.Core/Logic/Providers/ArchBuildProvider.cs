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

    public async Task FetchSourcesAsync(AuroraManifest manifest, string downloadDir, bool skipGpg)
    {
        // 1. Download
        var sourceMgr = new SourceManager();
        foreach (var sourceStr in manifest.Build.Source)
        {
            var entry = new SourceEntry(sourceStr);
            await sourceMgr.FetchSourceAsync(entry, downloadDir, msg => 
            {
                AnsiConsole.MarkupLine($"  [grey]-> {msg}[/]");
            });
        }

        // 2. Verify Checksums
        var integrity = new IntegrityManager();
        integrity.VerifyChecksums(manifest, downloadDir);

        // 3. Verify Signatures (Only if NOT skipped)
        if (!skipGpg)
        {
            var sigVerifier = new SignatureVerifier();
            sigVerifier.VerifySignatures(manifest, downloadDir);
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

        var cacheDir = Path.Combine(absoluteStartDir, "SRCDEST"); 
        var srcDir = Path.Combine(absoluteBuildDir, "src");
        Directory.CreateDirectory(srcDir);

        await extractor.ExtractAllAsync(manifest, cacheDir, srcDir);

        // Pass the logAction to the execution manager
        await exec.RunBuildFunctionAsync("prepare", logAction);
        await exec.RunBuildFunctionAsync("build", logAction);
        
        if (manifest.Build.Environment.Contains("check"))
            await exec.RunBuildFunctionAsync("check", logAction);
        
        await exec.RunBuildFunctionAsync("package", logAction);
        
        var finalPkgDir = Path.Combine(absoluteBuildDir, "pkg", manifest.Package.Name);
        await ArtifactCreator.CreateAsync(manifest, finalPkgDir, absoluteStartDir);
    }
}