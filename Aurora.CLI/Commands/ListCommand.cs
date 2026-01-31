using Aurora.Core.State;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class ListCommand : ICommand
{
    public string Name => "list";
    public string Description => "List installed packages";

    public Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        using var db = new PackageDatabase(config.DbPath);
        var packages = db.GetAllPackages();

        AnsiConsole.MarkupLine($"[bold]Root:[/] {config.SysRoot}");
        AnsiConsole.MarkupLine($"[bold]Installed Packages ({packages.Count}):[/]");

        var table = new Table().AddColumn("Name").AddColumn("Version").AddColumn("Arch");
        foreach (var p in packages)
        {
            table.AddRow(p.Name, p.Version, p.Arch);
        }
        AnsiConsole.Write(table);
        return Task.CompletedTask;
    }
}