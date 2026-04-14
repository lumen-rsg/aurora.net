using System.Diagnostics;
using Aurora.Core.Logging;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class RecoverCommand : ICommand
{
    public string Name => "recover";
    public string Description => "Rebuild the RPM database indexes";

    public Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        AuLogger.Info("Recover: rebuilding RPM database...");
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
            AuLogger.Info("Recover: RPM database rebuilt successfully.");
            AnsiConsole.MarkupLine("[green bold]✔ Recovery complete.[/] Database is now clean.");
        }
        else
        {
            var errMsg = proc?.StandardError.ReadToEnd() ?? "Unknown error";
            AuLogger.Error($"Recover: RPM database rebuild failed: {errMsg}");
            AnsiConsole.MarkupLine($"[red bold]ERROR during manual recovery:[/] {Markup.Escape(errMsg)}");
        }

        return Task.CompletedTask;
    }
}