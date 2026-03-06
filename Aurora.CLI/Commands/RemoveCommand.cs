using System.Diagnostics;
using Aurora.Core.State;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class RemoveCommand : ICommand
{
    public string Name => "remove";
    public string Description => "Remove packages";

    public Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("Usage: remove <pkg1> [pkg2] ...");

        var toRemove = new List<string>();

        // Validate installation
        foreach (var pkg in args)
        {
            if (!RpmLocalDb.IsInstalled(pkg, config.SysRoot))
            {
                AnsiConsole.MarkupLine($"[yellow]Warning:[/] Package [bold]{pkg}[/] is not installed.");
            }
            else
            {
                toRemove.Add(pkg);
            }
        }

        if (toRemove.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]Nothing to remove.[/]");
            return Task.CompletedTask;
        }

        // Confirm
        AnsiConsole.Write(new Rule("[red]Package Removal[/]").RuleStyle("grey"));
        var table = new Table().Border(TableBorder.Rounded).AddColumn("Target");
        foreach(var p in toRemove) table.AddRow($"[red bold]{p}[/]");
        AnsiConsole.Write(table);

        if (!config.AssumeYes && !AnsiConsole.Confirm($"Remove {toRemove.Count} packages?")) 
            return Task.CompletedTask;

        // Execute
        var targets = string.Join(" ", toRemove);
        var psi = new ProcessStartInfo
        {
            FileName = "rpm",
            Arguments = $"--root {config.SysRoot} -evh {targets}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        AnsiConsole.MarkupLine("[blue]Executing transaction...[/]");

        using var process = Process.Start(psi);
        if (process == null) return Task.CompletedTask;

        process.OutputDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AnsiConsole.MarkupLine($"[grey]{Markup.Escape(e.Data)}[/]"); };
        process.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(e.Data)}[/]"); };

        process.WaitForExit();

        if (process.ExitCode == 0) AnsiConsole.MarkupLine($"[green bold]✔ Successfully removed packages.[/]");
        else AnsiConsole.MarkupLine($"[red bold]Removal failed (Exit Code {process.ExitCode}).[/]");

        return Task.CompletedTask;
    }
}