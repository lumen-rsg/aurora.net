using Aurora.Core.IO;
using Aurora.Core.Logic;
using Aurora.Core.Models;
using Aurora.Core.Net;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class InstallCommand : ICommand
{
    public string Name => "install";
    public string Description => "Install a package (from repo or local file)";

    public async Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("Usage: install <package_name_or_file>");
        string arg = args[0];

        // --- 1. DETERMINE MODE (Local File vs Repo Name) ---
        bool isLocalFile = File.Exists(arg) && (arg.EndsWith(".au") || arg.EndsWith(".tar.gz") || arg.EndsWith(".tar"));
        
        Package? targetPkg = null;
        string? localFilePath = null;

        if (isLocalFile)
        {
            localFilePath = Path.GetFullPath(arg);
            AnsiConsole.MarkupLine($"[blue]Detected Local Package:[/] {localFilePath}");
            try 
            {
                // Extract metadata immediately to know what we are dealing with
                targetPkg = PackageExtractor.ReadManifest(localFilePath);
                AnsiConsole.MarkupLine($"Identified: [cyan]{targetPkg.Name}[/] v{targetPkg.Version}");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Failed to read local package:[/] {ex.Message}");
                return;
            }
        }
        else
        {
            // It's a package name (e.g. "acl")
            AnsiConsole.MarkupLine($"[blue]Target Package:[/] {arg}");
        }

        // --- 2. PREPARE ENVIRONMENT ---
        using var tx = new Transaction(config.DbPath);
        var installedPkgs = tx.GetAllPackages();
        var pkgName = targetPkg?.Name ?? arg; // Use name from file or arg

        if (!config.Force && tx.Database.IsInstalled(pkgName))
        {
            // Optional: Check if local file is newer/reinstall logic. For now, simple check.
            AnsiConsole.MarkupLine($"[yellow]Package '{pkgName}' is already installed.[/]");
            return;
        }

        // --- 3. LOAD REPOS (Needed for dependencies even in local mode) ---
        var availablePackages = new List<Package>();
        if (Directory.Exists(config.RepoDir))
        {
            foreach (var file in Directory.GetFiles(config.RepoDir, "*.yaml"))
            {
                try { availablePackages.AddRange(Aurora.Core.Parsing.PackageParser.ParseRepository(File.ReadAllText(file))); } catch {}
            }
        }

        // --- 4. INJECT LOCAL PACKAGE INTO SOLVER ---
        if (isLocalFile && targetPkg != null)
        {
            // If the repo also has 'acl', we overwrite it with our local version 
            // in the available list so the solver picks OURS.
            availablePackages.RemoveAll(p => p.Name == targetPkg.Name);
            availablePackages.Add(targetPkg);
        }
        else if (availablePackages.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No repository data found. Run 'sync' first.[/]");
            return;
        }

        // --- 5. SOLVE ---
        AnsiConsole.MarkupLine("Resolving dependencies...");
        var solver = new DependencySolver(availablePackages, installedPkgs);
        List<Package> plan;

        try
        {
            plan = solver.Resolve(pkgName);
        }
        catch (Exception ex)
        {
            if (config.Force)
            {
                AnsiConsole.MarkupLine($"[yellow]Solver Warning:[/] {ex.Message}");
                AnsiConsole.MarkupLine("[bold red]FORCE ENABLED: Installing single package.[/]");
                
                if (isLocalFile && targetPkg != null)
                {
                    targetPkg.IsBroken = true;
                    plan = new List<Package> { targetPkg };
                }
                else
                {
                    var found = availablePackages.FirstOrDefault(p => p.Name == pkgName);
                    if (found == null) throw new FileNotFoundException($"Package '{pkgName}' not found.");
                    found.IsBroken = true;
                    plan = new List<Package> { found };
                }
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Resolution failed:[/] {ex.Message}");
                return;
            }
        }

        // --- 6. CONFLICT CHECK ---
        try { ConflictValidator.Validate(plan, installedPkgs); }
        catch (Exception ex)
        {
            if (!config.Force) { AnsiConsole.MarkupLine($"[red bold]Transaction Aborted:[/] {ex.Message}"); return; }
            AnsiConsole.MarkupLine($"[yellow]Ignoring Conflict:[/] {ex.Message}");
        }

        AnsiConsole.MarkupLine($"[bold]Transaction Plan ({plan.Count}):[/] {string.Join(", ", plan.Select(p => p.Name))}");
        if (!config.AssumeYes && !AnsiConsole.Confirm("Proceed with installation?")) return;

        // --- 7. DOWNLOAD / ACQUIRE ---
        var repoMgr = new RepoManager(config.SysRoot);
        var packageFiles = new Dictionary<string, string>(); 

        // If local, map the local path immediately so we don't try to download it
        if (isLocalFile && targetPkg != null && localFilePath != null)
        {
            packageFiles[targetPkg.Name] = localFilePath;
        }

        await AnsiConsole.Status().StartAsync("Acquiring packages...", async ctx => 
        {
            foreach (var pkg in plan)
            {
                // Skip if we already have it (the local file)
                if (packageFiles.ContainsKey(pkg.Name)) continue;

                ctx.Status($"Downloading {pkg.Name}...");
                var path = await repoMgr.DownloadPackageAsync(pkg, config.CacheDir, msg => { });
                if (path == null) throw new FileNotFoundException($"Package {pkg.Name} not found in repos.");
                packageFiles[pkg.Name] = path;
            }
        });

        // --- 8. INSTALL ---
        await AnsiConsole.Status().StartAsync("Installing...", async ctx =>
        {
            foreach (var pkg in plan)
            {
                ctx.Status($"Installing {pkg.Name}...");
                var pkgFile = packageFiles[pkg.Name];

                // Extract & Run Pre-Install
                var tempScript = PackageInstaller.ExtractScript(pkgFile, Path.GetTempPath());
                if (tempScript != null)
                {
                    AnsiConsole.MarkupLine($"[grey]Running pre-install script for {pkg.Name}...[/]");
                    ScriptRunner.RunScript(tempScript, "pre_install", config.SysRoot);
                }

                var manifestFiles = new List<string>();
                PackageInstaller.InstallPackage(pkgFile, config.SysRoot, (physical, manifest) => 
                {
                    tx.AppendToJournal(physical);
                    manifestFiles.Add(manifest);
                });

                pkg.Files = manifestFiles;

                // Run Post-Install & Persist
                if (tempScript != null)
                {
                    AnsiConsole.MarkupLine($"[grey]Running post-install script for {pkg.Name}...[/]");
                    ScriptRunner.RunScript(tempScript, "post_install", config.SysRoot);
                    
                    Directory.CreateDirectory(config.ScriptDir);
                    var savedPath = Path.Combine(config.ScriptDir, $"{pkg.Name}.sh");
                    File.Copy(tempScript, savedPath, overwrite: true);
                    File.Delete(tempScript);
                }
                
                if (config.Force && pkg.Name == pkgName) pkg.IsBroken = true;
                tx.RegisterPackage(pkg);
            }
        });

        tx.Commit();
        AnsiConsole.MarkupLine($"[green]Successfully installed {pkgName}.[/]");
    }
}