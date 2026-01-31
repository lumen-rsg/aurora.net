using Aurora.Core.IO;
using Aurora.Core.Logic;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class InitCommand : ICommand
{
    public string Name => "init";
    public string Description => "Initialize Aurora root";

    public Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        var dirs = new[] { "var/lib/aurora", "var/cache/aurora", "usr/bin", "usr/lib", "etc" };
        foreach (var d in dirs)
        {
            Directory.CreateDirectory(PathHelper.GetPath(config.SysRoot, d));
        }

        using var tx = new Transaction(config.DbPath);
        tx.Commit();
        AnsiConsole.MarkupLine($"[green]Initialized Aurora root at {config.SysRoot}[/]");
        return Task.CompletedTask;
    }
}