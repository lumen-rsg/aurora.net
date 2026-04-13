using System.Diagnostics;
using Aurora.Core.Logging;
using Aurora.Core.State;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class RemoveCommand : ICommand
{
    public string Name => "remove";
    public string Description => "Remove packages";

    public async Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("Usage: remove <pkg1> [pkg2] ...");

        var toRemove = new List<string>();
        var installedVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Validate installation and capture current versions for history
        var installedPkgs = RpmLocalDb.GetInstalledPackages(config.SysRoot);
        foreach (var pkg in args)
        {
            if (!RpmLocalDb.IsInstalled(pkg, config.SysRoot))
            {
                AnsiConsole.MarkupLine($"[yellow]Warning:[/] Package [bold]{pkg}[/] is not installed.");
            }
            else
            {
                toRemove.Add(pkg);
                // Capture current version for history recording
                var match = installedPkgs.FirstOrDefault(p => p.Name.Equals(pkg, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    installedVersions[pkg] = match.FullVersion;
            }
        }

        if (toRemove.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]Nothing to remove.[/]");
            return;
        }

        // Confirm
        AnsiConsole.Write(new Rule("[red]Package Removal[/]").RuleStyle("grey"));
        var table = new Table().Border(TableBorder.Rounded).AddColumn("Target").AddColumn("Version");
        foreach(var p in toRemove)
        {
            var ver = installedVersions.GetValueOrDefault(p, "unknown");
            table.AddRow($"[red bold]{p}[/]", $"[grey]{Markup.Escape(ver)}[/]");
        }
        AnsiConsole.Write(table);

        if (!config.AssumeYes && !AnsiConsole.Confirm($"Remove {toRemove.Count} packages?")) 
            return;

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
        if (process == null) return;

        process.OutputDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AnsiConsole.MarkupLine($"[grey]{Markup.Escape(e.Data)}[/]"); };
        process.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(e.Data)}[/]"); };

        process.WaitForExit();

        if (process.ExitCode == 0)
        {
            AnsiConsole.MarkupLine($"[green bold]✔ Successfully removed packages.[/]");
            
            // Record in history
            try
            {
                var historyEntries = toRemove.Select(p => new HistoryEntry
                {
                    Action = "remove",
                    PackageName = p,
                    OldVersion = installedVersions.GetValueOrDefault(p),
                    Arch = installedPkgs.FirstOrDefault(ip => ip.Name.Equals(p, StringComparison.OrdinalIgnoreCase))?.Arch ?? ""
                });
                await TransactionHistory.RecordTransactionAsync(config.DbPath, "remove", historyEntries);
            }
            catch (Exception histEx) { AuLogger.Error($"Failed to record history: {histEx.Message}"); }
        }
        else
        {
            AnsiConsole.MarkupLine($"[red bold]Removal failed (Exit Code {process.ExitCode}).[/]");
        }
    }
}
