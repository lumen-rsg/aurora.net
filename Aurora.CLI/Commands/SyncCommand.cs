using Aurora.Core.Net;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class SyncCommand : ICommand
{
    public string Name => "sync";
    public string Description => "Synchronize repository databases";

    public async Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        AnsiConsole.MarkupLine($"[blue]Synchronizing repositories...[/]");
        
        var repoMgr = new RepoManager(config.SysRoot)
        {
            SkipSignatureCheck = config.SkipGpg // Use the same flag
        };

        await AnsiConsole.Live(new Table().Border(TableBorder.None).AddColumn("Repository").AddColumn("Status"))
            .StartAsync(async ctx =>
            {
                var table = new Table().AddColumn("Repository").AddColumn("Status");
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