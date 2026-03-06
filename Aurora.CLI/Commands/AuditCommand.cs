using System.Diagnostics;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class AuditCommand : ICommand
{
    public string Name => "audit";
    public string Description => "Check system health and file integrity";

    public Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        AnsiConsole.MarkupLine("[blue]Verifying RPM database and file integrity...[/]");
        AnsiConsole.MarkupLine("[grey]This may take a few minutes as it hashes every file on the system.[/]");

        var psi = new ProcessStartInfo
        {
            FileName = "rpm",
            Arguments = $"--root {config.SysRoot} -Va",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) return Task.CompletedTask;

        bool foundIssues = false;

        process.OutputDataReceived += (s, e) => 
        { 
            if (!string.IsNullOrWhiteSpace(e.Data)) 
            {
                foundIssues = true;
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(e.Data)}[/]"); 
            }
        };

        process.BeginOutputReadLine();
        process.WaitForExit();

        if (!foundIssues && process.ExitCode == 0)
        {
            AnsiConsole.MarkupLine("[green]✔ System is perfectly healthy.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[red]Audit complete. Issues found above.[/]");
            AnsiConsole.MarkupLine("[grey]Reference: S=Size, M=Mode, 5=MD5, D=Device, L=Symlink, U=User, G=Group, T=Mtime[/]");
        }

        return Task.CompletedTask;
    }
}