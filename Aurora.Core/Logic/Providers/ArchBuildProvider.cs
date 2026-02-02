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
        
        AnsiConsole.MarkupLine("[bold]Fetching sources...[/]");

        await AnsiConsole.Progress()
            .Columns(new ProgressColumn[] 
            {
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new DownloadedColumn(),
                new SpinnerColumn(),
            })
            .StartAsync(async ctx => 
            {
                foreach (var sourceStr in manifest.Build.Source)
                {
                    var entry = new SourceEntry(sourceStr);
                    
                    // Display filename and the URL in brackets
                    var taskDescription = $"[grey]{Markup.Escape(entry.FileName)}[/] [dim]({Markup.Escape(entry.Url)})[/]";
                    var task = ctx.AddTask(taskDescription);

                    try 
                    {
                        
                        await sourceMgr.FetchSourceAsync(entry, downloadDir, (total, current) => 
                        {
                            if (total.HasValue && total.Value > 0)
                            {
                                task.MaxValue = total.Value;
                                task.Value = current;
                            }
                            else
                            {
                                // SERVER DID NOT PROVIDE SIZE (Chunked)
                                // We set a fake MaxValue so the percentage doesn't show 0/100, 
                                // but we keep the bar indeterminate to show movement.
                                task.IsIndeterminate = true;
        
                                // Update description to show raw downloaded amount since we can't show %
                                task.Description = $"[grey]{Markup.Escape(entry.FileName)}[/] [blue]({current / 1024} KB downloaded)[/]";
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]Error downloading {entry.FileName}:[/] {Markup.Escape(ex.Message)}");
                        throw; // Stop the build
                    }
                    
                    task.StopTask();
                }
            });

        // Integrity checks follow...
        var integrity = new IntegrityManager();
        integrity.VerifyChecksums(manifest, downloadDir, startDir);
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
            try 
            {
                AnsiConsole.MarkupLine($"\n[bold magenta]Packaging Phase:[/] [white]{name}[/]");
            
                string funcName = (packageNames.Count > 1 || name != manifest.Package.Name) 
                    ? $"package_{name}" 
                    : "package";

                var subManifest = await exec.RunPackageFunctionAsync(funcName, manifest, logAction);

                var subPkgDir = Path.Combine(absoluteBuildDir, "pkg", name);
                await ArtifactCreator.CreateAsync(subManifest, subPkgDir, absoluteStartDir);
            }
            catch (Exception ex)
            {
                // Re-throw to be caught by the BuildCommand's UI handler
                throw new Exception($"Failed to package '{name}': {ex.Message}");
            }
        }

        AnsiConsole.MarkupLine("\n[green bold]✔ Build process completed successfully![/]");
    }
}