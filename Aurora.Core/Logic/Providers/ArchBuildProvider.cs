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

    public async Task FetchSourcesAsync(AuroraManifest manifest, string downloadDir, bool skipGpg, string startDir)
    {
        if (!Directory.Exists(downloadDir)) Directory.CreateDirectory(downloadDir);
        var sourceMgr = new SourceManager(startDir);

        await AnsiConsole.Progress()
            .Columns(new ProgressColumn[] 
            {
                new TaskDescriptionColumn(),    // Filename
                new ProgressBarColumn(),        // Visual bar
                new PercentageColumn(),         // 45%
                new DownloadedColumn(),         // 12MB / 40MB
                new SpinnerColumn(),            // Spinner
            })
            .StartAsync(async ctx => 
            {
                foreach (var sourceStr in manifest.Build.Source)
                {
                    var entry = new SourceEntry(sourceStr);
                    var task = ctx.AddTask($"[grey]{entry.FileName}[/]");

                    await sourceMgr.FetchSourceAsync(entry, downloadDir, (total, current) => 
                    {
                        if (total.HasValue)
                        {
                            task.MaxValue = total.Value;
                            task.Value = current;
                        }
                        else
                        {
                            // If server doesn't provide size, just spin
                            task.IsIndeterminate = true;
                        }
                    });
                    
                    task.StopTask();
                }
            });
        
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
        // 1. Load System Build Configuration (/etc/makepkg.conf)
        var sysConfig = await MakepkgConfigLoader.LoadAsync();

        // 2. Check Build Dependencies (Lumina Host-Audit Stub)
        DependencyStub.CheckBuildDependencies(manifest.Dependencies.Build);

        // 3. Setup Absolute Paths
        var absoluteBuildDir = Path.GetFullPath(buildDir);
        var absoluteStartDir = Path.GetFullPath(startDir);
        var srcDir = Path.Combine(absoluteBuildDir, "src");
        var cacheDir = Path.Combine(absoluteStartDir, "SRCDEST");

        // 4. Initialize Core Managers
        var exec = new ExecutionManager(absoluteBuildDir, absoluteStartDir, manifest, sysConfig);
        var extractor = new SourceExtractor();
        
        // 5. Cleanup and Prepare Sandboxed Directory Structure
        exec.PrepareDirectories();
        if (!Directory.Exists(srcDir)) Directory.CreateDirectory(srcDir);

        // 6. Source Extraction Phase
        // This handles symlinking local files and unpacking remote archives
        await extractor.ExtractAllAsync(manifest, cacheDir, srcDir, absoluteStartDir);

        // 7. Run Standard Build Lifecycle (User Space)
        await exec.RunBuildFunctionAsync("prepare", logAction);
        await exec.RunBuildFunctionAsync("build", logAction);
        
        if (manifest.Build.Environment.Contains("check") || manifest.Build.Options.Contains("check"))
        {
            await exec.RunBuildFunctionAsync("check", logAction);
        }

        // 8. Run Packaging Phase (Fakeroot Space)
        // Detect all packages produced by this PKGBUILD (Split Packages)
        var packageNames = manifest.Package.AllNames.Count > 0 
            ? manifest.Package.AllNames 
            : new List<string> { manifest.Package.Name };

        foreach (var name in packageNames)
        {
            AnsiConsole.MarkupLine($"\n[bold magenta]Packaging Phase:[/] [white]{name}[/]");
            
            // Determine function name: 'package' for single, 'package_name' for split
            string funcName = (packageNames.Count > 1 || name != manifest.Package.Name) 
                ? $"package_{name}" 
                : "package";

            // Execute package function in fakeroot and capture metadata overrides
            var subManifest = await exec.RunPackageFunctionAsync(funcName, manifest, logAction);

            // 9. Final Artifact Creation
            // Compress the specific subPkgDir into the final .au file
            var subPkgDir = Path.Combine(absoluteBuildDir, "pkg", name);
            await ArtifactCreator.CreateAsync(subManifest, subPkgDir, absoluteStartDir);
        }

        AnsiConsole.MarkupLine("\n[green bold]✔ Build process completed successfully![/]");
    }
}