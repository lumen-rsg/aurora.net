using Aurora.Core.Logging;
using Aurora.Core.State;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class ListCommand : ICommand
{
    public string Name => "list";
    public string Description => "List installed packages";

    public Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        var packages = RpmLocalDb.GetInstalledPackages(config.SysRoot);

        AuLogger.Info($"List: {packages.Count} installed packages listed.");
        AnsiConsole.MarkupLine($"[bold]Root:[/] {config.SysRoot}");
        AnsiConsole.MarkupLine($"[bold]Installed Packages ({packages.Count}):[/]");

        var table = new Table().AddColumn("Name").AddColumn("Version").AddColumn("Arch");
        foreach (var p in packages.OrderBy(p => p.Name))
        {
            table.AddRow(p.Name, p.FullVersion, p.Arch);
        }
        AnsiConsole.Write(table);
        
        return Task.CompletedTask;
    }
}