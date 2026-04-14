using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Aurora.Core.IO;
using Aurora.Core.Logging;
using Aurora.Core.Net;
using Aurora.Core.Parsing;
using Aurora.Core.State;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class SyncCommand : ICommand
{
    public string Name => "sync";
    public string Description => "Synchronize RPM repository databases";

    public async Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        AuLogger.Info("Sync: synchronizing repositories...");
        AnsiConsole.MarkupLine($"[blue]Synchronizing repositories...[/]");
        
        var reposDir = PathHelper.GetPath(config.SysRoot, "etc/yum.repos.d");
        var dbDir = PathHelper.GetPath(config.SysRoot, "var/lib/aurora");
        
        if (!Directory.Exists(reposDir) || !Directory.EnumerateFiles(reposDir, "*.repo").Any())
        {
            AuLogger.Warn("Sync: no repository configurations found.");
            AnsiConsole.MarkupLine("[yellow]Warning:[/] No repository configurations found at [blue]etc/yum.repos.d/[/]");
            return;
        }

        var repos = RepoConfigParser.ParseDirectory(reposDir);

        // --- STALE DATABASE CLEANUP ---
        if (Directory.Exists(dbDir))
        {
            var existingDbs = Directory.GetFiles(dbDir, "*.sqlite");
            foreach (var dbFile in existingDbs)
            {
                var repoId = Path.GetFileNameWithoutExtension(dbFile);
                if (!repos.ContainsKey(repoId) || !repos[repoId].Enabled)
                {
                    AnsiConsole.MarkupLine($"[grey]Removing stale database:[/] {repoId}");
                    try { File.Delete(dbFile); } catch { }
                }
            }
        }

        var repoMgr = new RepoManager(config.SysRoot) { SkipSignatureCheck = config.SkipGpg };

        await AnsiConsole.Live(new Table().Border(TableBorder.Rounded).AddColumn("Repository").AddColumn("Status"))
            .StartAsync(async ctx =>
            {
                var table = new Table().Border(TableBorder.Rounded).AddColumn("Repository").AddColumn("Status");
                ctx.UpdateTarget(table);

                await repoMgr.SyncRepositoriesAsync((name, status) =>
                {
                    string color = status switch {
                        var s when s.Contains("Done") => "green",
                        var s when s.Contains("Failed") => "red",
                        _ => "yellow"
                    };
                    
                    table.AddRow(name, $"[{color}]{status}[/]");
                    ctx.UpdateTarget(table);
                });
            });

        // Sync comps (package groups) data
        AnsiConsole.MarkupLine("[blue]Syncing package groups...[/]");
        await repoMgr.SyncCompsAsync((name, status) =>
        {
            AnsiConsole.MarkupLine($"  [grey]{name}:[/] {status}");
        });

        // Invalidate repo cache so subsequent commands use fresh data
        RepoLoader.InvalidateCache(dbDir);

        AuLogger.Info("Sync: complete.");
        AnsiConsole.MarkupLine("[green]Sync complete.[/]");
    }
}
