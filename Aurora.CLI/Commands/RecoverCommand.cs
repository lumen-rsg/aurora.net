using System.Diagnostics;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class RecoverCommand : ICommand
{
    public string Name => "recover";
    public string Description => "Rebuild the RPM database indexes";

    public Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        AnsiConsole.MarkupLine("[yellow]Attempting to rebuild RPM database...[/]");
        
        var psi = new ProcessStartInfo
        {
            FileName = "rpm",
            Arguments = $"--root {config.SysRoot} --rebuilddb",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        proc?.WaitForExit();

        if (proc?.ExitCode == 0)
        {
            AnsiConsole.MarkupLine("[green bold]✔ Recovery complete.[/] Database is now clean.");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red bold]ERROR during manual recovery:[/] {Markup.Escape(proc?.StandardError.ReadToEnd() ?? "Unknown error")}");
        }

        return Task.CompletedTask;
    }
}