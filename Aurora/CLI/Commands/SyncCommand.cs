using Aurora.Core.Net;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class SyncCommand : ICommand
{
    public string Name => "sync";
    public string Description => "Sync repositories";

    public async Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        AnsiConsole.MarkupLine($"[blue]Syncing repositories (Root: {config.SysRoot})...[/]");
        var repoMgr = new RepoManager(config.SysRoot);
        
        // HACK: Property injection or constructor update preferred, 
        // but let's just use a field in RepoManager for this session.
        repoMgr.SkipSignatureCheck = config.SkipSig; 

        await AnsiConsole.Live(new Table().AddColumn("Repo").AddColumn("Status"))
            .StartAsync(async ctx =>
            {
                var table = new Table().AddColumn("Repository").AddColumn("Status");
                ctx.UpdateTarget(table);

                await repoMgr.SyncRepositoriesAsync((name, status) =>
                {
                    var color = status.StartsWith("Failed") ? "red" : "green";
                    if (status == "Downloading...") color = "yellow";
                    table.AddRow(name, $"[{color}]{status}[/]");
                    ctx.UpdateTarget(table);
                });
            });
        
        AnsiConsole.MarkupLine("[green]Sync complete.[/]");
    }
}