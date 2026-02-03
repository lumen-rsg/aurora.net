using Aurora.Core.Net;
using Aurora.Core.IO;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class SyncCommand : ICommand
{
    public string Name => "sync";
    public string Description => "Synchronize repository databases";

    public async Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        AnsiConsole.MarkupLine($"[blue]Synchronizing repositories...[/]");
        
        // Ensure the internal aurora directory exists
        var dbDir = PathHelper.GetPath(config.SysRoot, "var/lib/aurora");
        if (!Directory.Exists(dbDir)) Directory.CreateDirectory(dbDir);

        var repoMgr = new RepoManager(config.SysRoot)
        {
            SkipSignatureCheck = config.SkipGpg
        };

        // Check if config exists before starting the UI
        var configPath = PathHelper.GetPath(config.SysRoot, "etc/aurora/repolist");
        if (!File.Exists(configPath))
        {
            AnsiConsole.MarkupLine("[yellow]Warning:[/] No repository list found at [blue]etc/aurora/repolist[/]");
            AnsiConsole.MarkupLine("[grey]Please create a repository configuration file to continue.[/]");
            return;
        }

        await AnsiConsole.Live(new Table().Border(TableBorder.Rounded).AddColumn("Repository").AddColumn("Status"))
            .StartAsync(async ctx =>
            {
                var table = new Table().Border(TableBorder.Rounded).AddColumn("Repository").AddColumn("Status");
                ctx.UpdateTarget(table);

                bool anyFound = false;
                await repoMgr.SyncRepositoriesAsync((name, status) =>
                {
                    anyFound = true;
                    string color = status switch {
                        var s when s.Contains("Done") => "green",
                        var s when s.Contains("Failed") => "red",
                        _ => "yellow"
                    };
                    
                    table.AddRow(name, $"[{color}]{status}[/]");
                    ctx.UpdateTarget(table);
                });

                if (!anyFound)
                {
                    table.AddRow("[yellow]None[/]", "[grey]No enabled repositories found in repolist[/]");
                    ctx.UpdateTarget(table);
                }
            });

        AnsiConsole.MarkupLine("[green]Sync complete.[/]");
    }
}