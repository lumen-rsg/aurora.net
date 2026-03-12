using System.Diagnostics;
using Aurora.Core.Logic;
using Aurora.Core.Models;
using Aurora.Core.Net;
using Aurora.Core.State;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class InstallCommand : ICommand
{
    public string Name => "install";
    public string Description => "Install packages (repo or local .rpm)";

    public async Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("Usage: install <pkg1> [pkg2] ...");
        
        // 1. Check Mode: Local Files vs Repo Packages
        // For simplicity in V1, we assume if the first arg ends in .rpm, all are files.
        bool isLocalFileMode = args.All(a => a.EndsWith(".rpm"));

        if (isLocalFileMode)
        {
            AnsiConsole.MarkupLine($"[blue]Installing {args.Length} local package(s)...[/]");
            if (!config.AssumeYes && !AnsiConsole.Confirm("Proceed with installation?")) return;
            
            // Pass the array of files to RPM
            var fullPaths = args.Select(Path.GetFullPath).ToList();
            SystemUpdater.ApplyUpdates(fullPaths, config.SysRoot, config.Force, msg => AnsiConsole.MarkupLine($"[grey]{Markup.Escape(msg)}[/]"));
            AnsiConsole.MarkupLine($"[green bold]✔ Installed local packages successfully.[/]");
            return;
        }

        // --- Repo Install Path ---
        
        // Filter out packages that are already installed (unless --force)
        var targetsToResolve = new List<string>();
        foreach (var arg in args)
        {
            if (RpmLocalDb.IsInstalled(arg, config.SysRoot) && !config.Force)
            {
                AnsiConsole.MarkupLine($"[yellow]Skipping [bold]{arg}[/]: already installed.[/]");
            }
            else
            {
                targetsToResolve.Add(arg);
            }
        }

        if (targetsToResolve.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]Nothing to do.[/]");
            return;
        }

        var availablePackages = new List<Package>();
        var installedPkgs = RpmLocalDb.GetInstalledPackages(config.SysRoot);

        // 1. Load Repos
        var repoFiles = Directory.GetFiles(config.RepoDir, "*.sqlite");
        if (repoFiles.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No repository databases found. Run 'au sync' first.");
            return;
        }

        int loadedCount = 0;
        await AnsiConsole.Status().StartAsync("Reading repositories...", async ctx =>
        {
            foreach (var dbFile in repoFiles)
            {
                try
                {
                    using var db = new RpmRepoDb(dbFile);
                    string repoId = Path.GetFileNameWithoutExtension(dbFile); 
                    var pkgs = db.GetAllPackages(repoId);
                    availablePackages.AddRange(pkgs);
                    loadedCount += pkgs.Count;
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error reading {Path.GetFileName(dbFile)}:[/] {ex.Message}");
                }
            }
        });

        AnsiConsole.MarkupLine($"[grey]Loaded {loadedCount} packages from {repoFiles.Length} repositories.[/]");

        // 2. Resolve Dependencies (Pass the list!)
        List<Package> plan;
        try
        {
            var solver = new DependencySolver(availablePackages, installedPkgs);
            plan = solver.Resolve(targetsToResolve); // <--- Passing List<string>
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red bold]Dependency Error:[/] {Markup.Escape(ex.Message)}");
            return;
        }

        // 3. Confirm
        PrintTransactionSummary(plan);
        if (!config.AssumeYes && !AnsiConsole.Confirm("Proceed with installation?")) return;

        // 4. Download (Parallel)
        var repoMgr = new RepoManager(config.SysRoot) { SkipSignatureCheck = config.SkipGpg };
        var packagePaths = new System.Collections.Concurrent.ConcurrentBag<string>();
        var semaphore = new SemaphoreSlim(5);

        if (!Directory.Exists(config.CacheDir)) Directory.CreateDirectory(config.CacheDir);

        await AnsiConsole.Progress()
            .Columns(new ProgressColumn[] { new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new DownloadedColumn(), new SpinnerColumn() })
            .StartAsync(async ctx =>
            {
                var tasks = plan.Select(async pkg =>
                {
                    await semaphore.WaitAsync();
                    var task = ctx.AddTask($"[grey]{pkg.Name}[/]");
                    try
                    {
                        var path = await repoMgr.DownloadPackageAsync(pkg, config.CacheDir, (total, current) =>
                        {
                            if (total.HasValue) { task.MaxValue = total.Value; task.Value = current; }
                            else task.IsIndeterminate = true;
                        });
                        if (path == null) throw new FileNotFoundException($"Package {pkg.Name} not found.");
                        packagePaths.Add(path);
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]Download failed for {pkg.Name}:[/] {ex.Message}");
                        throw;
                    }
                    finally { task.StopTask(); semaphore.Release(); }
                });
                await Task.WhenAll(tasks);
            });

        // 5. Execute
        AnsiConsole.Write(new Rule("[green]Installing[/]").RuleStyle("grey"));
        try
        {
            SystemUpdater.ApplyUpdates(packagePaths.ToList(), config.SysRoot, config.Force, msg => AnsiConsole.MarkupLine($"[grey]{Markup.Escape(msg)}[/]"));
            AnsiConsole.MarkupLine($"\n[green bold]✔ Transaction successful.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red bold]Installation Failed:[/] {Markup.Escape(ex.Message)}");
        }
    }

    private void PrintTransactionSummary(List<Package> plan)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Package");
        table.AddColumn("Version");
        table.AddColumn("Size");
        long totalSize = 0;
        foreach (var p in plan)
        {
            table.AddRow($"[cyan]{p.Name}[/]", $"[grey]{p.FullVersion}[/]", FormatBytes(p.Size));
            totalSize += p.Size;
        }
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"Total Download Size: [bold green]{FormatBytes(totalSize)}[/]\n");
    }

    private string FormatBytes(long bytes)
    {
        string[] suffix = { "B", "KB", "MB", "GB", "TB" };
        int i; double dblSByte = bytes;
        for (i = 0; i < suffix.Length && bytes >= 1024; i++, bytes /= 1024) dblSByte = bytes / 1024.0;
        return string.Format("{0:0.00} {1}", dblSByte, suffix[i]);
    }
}