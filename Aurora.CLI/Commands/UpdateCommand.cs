using Aurora.Core.Logic;
using Aurora.Core.Logging;
using Aurora.Core.Models;
using Aurora.Core.Net;
using Aurora.Core.State;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class UpdateCommand : ICommand
{
    public string Name => "update";
    public string Description => "Update system packages from repositories";

    public async Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        if (!Directory.Exists(config.RepoDir))
        {
            AnsiConsole.MarkupLine($"[red]No repository directory found at {config.RepoDir}.[/]");
            return;
        }

        var repoFiles = Directory.GetFiles(config.RepoDir, "*.sqlite");
        if (repoFiles.Length == 0)
        {
            AnsiConsole.MarkupLine($"[red]No repository databases found. Run 'au sync' first.[/]");
            return;
        }

        var availablePackages = new List<Package>();

        await AnsiConsole.Status().StartAsync("Reading repositories...", async ctx =>
        {
            foreach (var file in repoFiles)
            {
                try
                {
                    using var db = new RpmRepoDb(file);
                    string repoId = Path.GetFileNameWithoutExtension(file); 
                    availablePackages.AddRange(db.GetAllPackages(repoId));
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[yellow]Warning: Failed to parse {Path.GetFileName(file)}: {ex.Message}[/]");
                }
            }
        });

        AnsiConsole.Status().Start("Calculating update plan...", _ => {});
        var updatePlan = SystemUpdater.CalculateUpdates(availablePackages, config.SysRoot);

        if (updatePlan.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]System is up to date.[/]");
            return;
        }

        AnsiConsole.Write(new Rule("[yellow]System Update[/]").RuleStyle("grey"));
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Package");
        table.AddColumn("Old Version");
        table.AddColumn("New Version");
        table.AddColumn("Download Size");

        long totalDownloadSize = 0;
        foreach (var pair in updatePlan)
        {
            table.AddRow($"[cyan]{pair.NewPkg.Name}[/]", $"[grey]{pair.OldVer}[/]", $"[green]{pair.NewVer}[/]", FormatBytes(pair.NewPkg.Size));
            totalDownloadSize += pair.NewPkg.Size;
        }
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"Total packages: [bold]{updatePlan.Count}[/]");
        AnsiConsole.MarkupLine($"Total download size: [bold green]{FormatBytes(totalDownloadSize)}[/]");

        if (!config.AssumeYes && !AnsiConsole.Confirm("Proceed with update?")) return;

        // Download
        var repoMgr = new RepoManager(config.SysRoot) { SkipSignatureCheck = config.SkipGpg };
        var packagePaths = new System.Collections.Concurrent.ConcurrentBag<string>();
        var semaphore = new SemaphoreSlim(5);

        if (!Directory.Exists(config.CacheDir)) Directory.CreateDirectory(config.CacheDir);

        AnsiConsole.Write(new Rule("[cyan]Downloading Updates[/]").RuleStyle("grey"));
        await AnsiConsole.Progress()
            .Columns(new ProgressColumn[] { new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new DownloadedColumn(), new SpinnerColumn() })
            .StartAsync(async ctx =>
            {
                var tasks = updatePlan.Select(async pair =>
                {
                    var pkg = pair.NewPkg;
                    await semaphore.WaitAsync();
                    var task = ctx.AddTask($"[grey]{pkg.Name}[/]");
                    try
                    {
                        var path = await repoMgr.DownloadPackageAsync(pkg, config.CacheDir, (total, current) =>
                        {
                            if (total.HasValue) { task.MaxValue = total.Value; task.Value = current; }
                            else task.IsIndeterminate = true;
                        });
                        if (path == null) throw new FileNotFoundException($"Update for {pkg.Name} not found.");
                        packagePaths.Add(path);
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]Download failed for {pkg.Name}:[/] {ex.Message}");
                        throw;
                    }
                    finally
                    {
                        task.StopTask();
                        semaphore.Release();
                    }
                });
                await Task.WhenAll(tasks);
            });

        // Install
        AnsiConsole.Write(new Rule("[green]Applying Updates[/]").RuleStyle("grey"));
        
        List<string> rpmLogs = new List<string>();
        try
        {
            AnsiConsole.Status().Start("[cyan]Applying updates...[/]", ctx => 
            {
                SystemUpdater.ApplyUpdates(packagePaths.ToList(), config.SysRoot, config.Force, 
                    msg => rpmLogs.Add(msg));
            });
            
            AnsiConsole.MarkupLine("\n[green bold]✔ System updated successfully.[/]");
            
            // Record in history
            try
            {
                var historyEntries = updatePlan.Select(pair => new HistoryEntry
                {
                    Action = "upgrade",
                    PackageName = pair.NewPkg.Name,
                    Epoch = pair.NewPkg.Epoch,
                    OldVersion = pair.OldVer,
                    NewVersion = pair.NewVer,
                    Arch = pair.NewPkg.Arch
                });
                await TransactionHistory.RecordTransactionAsync(config.DbPath, "update", historyEntries);
            }
            catch (Exception histEx) { AuLogger.Error($"Failed to record history: {histEx.Message}"); }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red bold]Update Failed:[/] {Markup.Escape(ex.Message)}");
            
            // Only show RPM logs on error - helpful for debugging
            if (rpmLogs.Count > 0)
            {
                AnsiConsole.Write(new Rule("[yellow]RPM Output[/]").RuleStyle("yellow"));
                foreach (var log in rpmLogs)
                {
                    AnsiConsole.MarkupLine($"[grey]{Markup.Escape(log)}[/]");
                }
            }
        }
    }

    private string FormatBytes(long bytes)
    {
        string[] suffix = { "B", "KB", "MB", "GB", "TB" };
        int i; double dblSByte = bytes;
        for (i = 0; i < suffix.Length && bytes >= 1024; i++, bytes /= 1024) dblSByte = bytes / 1024.0;
        return string.Format("{0:0.00} {1}", dblSByte, suffix[i]);
    }
}