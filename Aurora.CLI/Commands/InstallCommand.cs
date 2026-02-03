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
        AnsiConsole.MarkupLine("[grey]DEBUG: Starting install command...[/]");
        if (args.Length < 1) throw new ArgumentException("Usage: install <package_name_or_file>");
        string arg = args[0];

        bool isLocalFile = File.Exists(arg) && (arg.EndsWith(".au") || arg.EndsWith(".tar.gz") || arg.EndsWith(".tar"));
        Package? targetPkg = null;
        string? localFilePath = null;

        if (isLocalFile)
        {
            localFilePath = Path.GetFullPath(arg);
            AnsiConsole.MarkupLine($"[blue]Detected Local Package:[/] {localFilePath}");
            targetPkg = PackageExtractor.ReadManifest(localFilePath);
            AnsiConsole.MarkupLine($"Identified: [cyan]{targetPkg.Name}[/] v{targetPkg.Version}");
        }
        else
        {
            AnsiConsole.MarkupLine($"[blue]Target Package:[/] {arg}");
        }
        AnsiConsole.MarkupLine("[grey]DEBUG: Initializing transaction...[/]");
        using var tx = new Transaction(config.DbPath);
        var installedPkgs = tx.GetAllPackages();
        var pkgName = targetPkg?.Name ?? arg;
        AnsiConsole.MarkupLine("[green]DEBUG: Transaction OK.[/]");

        if (!config.Force && tx.Database.IsInstalled(pkgName))
        {
            AnsiConsole.MarkupLine($"[yellow]Package '{pkgName}' is already installed.[/]");
            return;
        }
        AnsiConsole.MarkupLine("[grey]DEBUG: Loading repository data...[/]");
        var availablePackages = new List<Package>();
        if (Directory.Exists(config.RepoDir))
        {
            // Be careful to only load the latest synced databases
            foreach (var file in Directory.GetFiles(config.RepoDir, "*.aurepo"))
            {
                try 
                {
                    var repoContent = File.ReadAllText(file);
                    var parsedRepo = Aurora.Core.Parsing.RepoParser.Parse(repoContent);
                    availablePackages.AddRange(parsedRepo.Packages);
                } 
                catch (Exception ex) 
                {
                    AnsiConsole.MarkupLine($"[yellow]Warning: Skipping {Path.GetFileName(file)}: {ex.Message}[/]");
                }
            }
        }
        AnsiConsole.MarkupLine("[green]DEBUG: Repositories loaded OK.[/]");

        if (isLocalFile && targetPkg != null)
        {
            availablePackages.RemoveAll(p => p.Name == targetPkg.Name);
            availablePackages.Add(targetPkg);
        }
        else if (availablePackages.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No repository data found. Run 'sync' first.[/]");
            return;
        }

        AnsiConsole.MarkupLine("[grey]DEBUG: Initializing dependency solver...[/]");
        AnsiConsole.MarkupLine("Resolving dependencies...");
        var solver = new DependencySolver(availablePackages, installedPkgs);
        AnsiConsole.MarkupLine("[green]DEBUG: Solver OK.[/]");
        AnsiConsole.MarkupLine("[grey]DEBUG: Resolving plan...[/]");
        List<Package> plan;
        try
        {
            plan = solver.Resolve(pkgName);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Resolution failed:[/] {ex.Message}");
            return;
        }
        AnsiConsole.MarkupLine("[green]DEBUG: Plan resolved OK.[/]");
        
        AnsiConsole.MarkupLine($"[bold]Transaction Plan ({plan.Count}):[/] {string.Join(", ", plan.Select(p => p.Name))}");
        if (!config.AssumeYes && !AnsiConsole.Confirm("Proceed with installation?")) return;

        // --- UPDATED DOWNLOAD PHASE ---
        AnsiConsole.MarkupLine("[grey]DEBUG: Initializing RepoManager for download...[/]");
        var repoMgr = new RepoManager(config.SysRoot);
        var packageFiles = new Dictionary<string, string>();

        if (isLocalFile && targetPkg != null && localFilePath != null)
        {
            packageFiles[targetPkg.Name] = localFilePath;
        }
        AnsiConsole.MarkupLine("[green]DEBUG: RepoManager OK.[/]");
        AnsiConsole.MarkupLine("[grey]DEBUG: Starting installation phase...[/]");

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
                foreach (var pkg in plan)
                {
                    if (packageFiles.ContainsKey(pkg.Name)) continue;

                    var task = ctx.AddTask($"[grey]{pkg.Name}[/]");
                    var path = await repoMgr.DownloadPackageAsync(pkg, config.CacheDir, (total, current) =>
                    {
                        if (total.HasValue && total.Value > 0)
                        {
                            task.MaxValue = total.Value;
                            task.Value = current;
                        }
                        else
                        {
                            task.IsIndeterminate = true;
                        }
                    });
                    
                    if (path == null) throw new FileNotFoundException($"Package {pkg.Name} not found in repos.");
                    packageFiles[pkg.Name] = path;
                    task.StopTask();
                }
            });

        // --- INSTALL PHASE ---
        await AnsiConsole.Status().StartAsync("Installing...", async ctx =>
        {
            foreach (var pkg in plan)
            {
                ctx.Status($"Installing {pkg.Name}...");
                var pkgFile = packageFiles[pkg.Name];
                var manifestFiles = new List<string>();

                PackageInstaller.InstallPackage(pkgFile, config.SysRoot, (physical, manifest) =>
                {
                    tx.AppendToJournal(physical);
                    manifestFiles.Add(manifest);
                });

                pkg.Files = manifestFiles;
                tx.RegisterPackage(pkg);
            }
        });

        tx.Commit();
        AnsiConsole.MarkupLine($"[green]Successfully installed {pkgName}.[/]");
    }
}