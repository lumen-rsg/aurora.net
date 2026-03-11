using Aurora.Core.Net;
using Aurora.Core.IO;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class SyncCommand : ICommand
{
    public string Name => "sync";
    public string Description => "Synchronize RPM repository databases";

    public async Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        AnsiConsole.MarkupLine($"[blue]Synchronizing repositories...[/]");
        
        // 1. New Path Logic: Check for etc/yum.repos.d
        var reposDir = PathHelper.GetPath(config.SysRoot, "etc/yum.repos.d");
        
        if (!Directory.Exists(reposDir) || !Directory.EnumerateFiles(reposDir, "*.repo").Any())
        {
            AnsiConsole.MarkupLine("[yellow]Warning:[/] No repository configurations found at [blue]etc/yum.repos.d/[/]");
            AnsiConsole.MarkupLine("[grey]Please create a .repo file (e.g., lumina.repo) inside the target root.[/]");
            return;
        }

        // 2. Initialize the RPM Repo Manager
        var repoMgr = new RepoManager(config.SysRoot)
        {
            SkipSignatureCheck = config.SkipGpg
        };

        // 3. UI Execution Loop
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

        AnsiConsole.MarkupLine("[green]Sync complete.[/]");
    }
}