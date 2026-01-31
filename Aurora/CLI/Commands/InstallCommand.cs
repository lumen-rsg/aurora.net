using Aurora.Core.IO;
using Aurora.Core.Logic;
using Aurora.Core.Net;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class InstallCommand : ICommand
{
    public string Name => "install";
    public string Description => "Install a package";

    public async Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("Usage: install <package>");
        string pkgName = args[0];

        AnsiConsole.MarkupLine($"[blue]Target Root:[/] {config.SysRoot}");

        using var tx = new Transaction(config.DbPath);
        var installedPkgs = tx.GetAllPackages();

        if (tx.Database.IsInstalled(pkgName))
        {
            AnsiConsole.MarkupLine($"[yellow]Package '{pkgName}' is already installed.[/]");
            return;
        }

        // 1. Load Repo Data
        var availablePackages = new List<Core.Models.Package>();
        if (Directory.Exists(config.RepoDir))
        {
            foreach (var file in Directory.GetFiles(config.RepoDir, "*.yaml"))
            {
                try { availablePackages.AddRange(Core.Parsing.PackageParser.ParseRepository(File.ReadAllText(file))); } catch {}
            }
        }

        if (availablePackages.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No repository data found. Run 'sync' first.[/]");
            return;
        }

        // 2. Solve
        AnsiConsole.MarkupLine("Resolving dependencies...");
        var solver = new DependencySolver(availablePackages, installedPkgs);
        List<Core.Models.Package> plan;

        try
        {
            plan = solver.Resolve(pkgName);
        }
        catch (Exception ex)
        {
            if (config.Force)
            {
                AnsiConsole.MarkupLine($"[red]Resolution Failed:[/] {ex.Message}");
                AnsiConsole.MarkupLine("[bold red]FORCE ENABLED: Installing single package.[/]");
                var targetPkg = availablePackages.FirstOrDefault(p => p.Name == pkgName);
                if (targetPkg == null) throw new FileNotFoundException($"Package '{pkgName}' not found in repo.");
                targetPkg.IsBroken = true;
                plan = new List<Core.Models.Package> { targetPkg };
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Resolution failed:[/] {ex.Message}");
                AnsiConsole.MarkupLine("[grey]Use --force to ignore.[/]");
                return;
            }
        }

        // 3. Conflict Validation
        try
        {
            ConflictValidator.Validate(plan, installedPkgs);
        }
        catch (Exception ex)
        {
            if (config.Force)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning:[/] {ex.Message}");
                AnsiConsole.MarkupLine("[bold red]FORCE ENABLED: Ignoring conflicts.[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red bold]Transaction Aborted:[/] {ex.Message}");
                return;
            }
        }

        AnsiConsole.MarkupLine($"[bold]Transaction Plan ({plan.Count}):[/] {string.Join(", ", plan.Select(p => p.Name))}");

        if (!config.AssumeYes && !AnsiConsole.Confirm("Proceed with installation?")) return;

        // 4. Download
        var repoMgr = new RepoManager(config.SysRoot);
        var packageFiles = new Dictionary<string, string>();

        await AnsiConsole.Status().StartAsync("Downloading packages...", async ctx =>
        {
            foreach (var pkg in plan)
            {
                ctx.Status($"Downloading {pkg.Name}...");
                var path = await repoMgr.DownloadPackageAsync(pkg, config.CacheDir, msg => { });
                packageFiles[pkg.Name] = path ?? throw new FileNotFoundException($"Package {pkg.Name} not found.");
            }
        });

        // 5. Install
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
                    AnsiConsole.MarkupLine("[grey]Running pre-install script...[/]");
                    ScriptRunner.RunScript(tempScript, "pre_install", config.SysRoot);
                }

                var manifestFiles = new List<string>();
                PackageInstaller.InstallPackage(pkgFile, config.SysRoot, (physical, manifest) =>
                {
                    tx.AppendToJournal(physical); 
                    manifestFiles.Add(manifest);
                });

                pkg.Files = manifestFiles;

                // Run Post-Install & Persist Script
                if (tempScript != null)
                {
                    AnsiConsole.MarkupLine("[grey]Running post-install script...[/]");
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