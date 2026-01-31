using Aurora.Core.Logic;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class RecoverCommand : ICommand
{
    public string Name => "recover";
    public string Description => "Manually recover from an interrupted transaction";

    public Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        if (!Transaction.HasPendingRecovery(config.DbPath))
        {
            AnsiConsole.MarkupLine("[green]No pending recovery required. System is clean.[/]");
            return Task.CompletedTask;
        }

        AnsiConsole.MarkupLine("[yellow]Attempting manual recovery...[/]");
        try
        {
            Transaction.RunRecovery(config.DbPath);
            AnsiConsole.MarkupLine("[green bold]Recovery complete.[/] System is now clean.");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red bold]ERROR during manual recovery:[/] {Markup.Escape(ex.Message)}");
        }
        return Task.CompletedTask;
    }
}