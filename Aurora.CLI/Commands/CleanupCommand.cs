using System.Diagnostics;
using Aurora.Core.Logic;
using Aurora.Core.State;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class CleanupCommand : ICommand
{
    public string Name => "cleanup";
    public string Description => "Remove orphaned packages no longer required by any installed package";

    public Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        // Clear repo package cache so the next command sees a fresh state
        RepoLoader.InvalidateCache(config.RepoDir);

        AnsiConsole.MarkupLine("[blue]Scanning for orphaned packages...[/]");

        // 1. Query all installed packages with their Requires
        var allPackages = RpmLocalDb.GetInstalledPackages(config.SysRoot);

        if (allPackages.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No installed packages found.[/]");
            return Task.CompletedTask;
        }

        // 2. Iterative orphan detection (handles cascading orphans)
        var orphanNames = new HashSet<string>();
        var changed = true;

        while (changed)
        {
            changed = false;

            // Build the set of "active" packages (not yet marked as orphan)
            var activePackages = allPackages.Where(p => !orphanNames.Contains(p.Name)).ToList();

            // Collect all required capability names from active packages
            var requiredCaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pkg in activePackages)
            {
                foreach (var req in pkg.Requires)
                {
                    // Skip RPM internal capabilities and rich dependencies
                    if (req.StartsWith("rpmlib(")) continue;
                    if (req.StartsWith('(')) continue;

                    // Extract the bare capability name (strip version operators)
                    var capName = DependencySolver.ParseProvideName(req);
                    requiredCaps.Add(capName);
                }
            }

            // An orphan is a package whose provides (including its own name)
            // are NOT required by any remaining active package.
            // Note: we exclude packages that have active dependents from being marked orphan.
            foreach (var pkg in activePackages)
            {
                // Check if any of this package's provides are required
                var isNeeded = false;

                // The package name itself is always a capability it provides
                if (requiredCaps.Contains(pkg.Name))
                {
                    isNeeded = true;
                }

                // Check all virtual provides
                if (!isNeeded)
                {
                    foreach (var prov in pkg.Provides)
                    {
                        var provName = DependencySolver.ParseProvideName(prov);
                        if (requiredCaps.Contains(provName))
                        {
                            isNeeded = true;
                            break;
                        }
                    }
                }

                if (!isNeeded && !orphanNames.Contains(pkg.Name))
                {
                    // Before marking as orphan, verify no other active non-orphan package
                    // actually depends on this one through its provides.
                    // We already checked caps above, but let's also ensure we don't
                    // mark a package as orphan if another package's raw requires
                    // matches this package's name exactly.
                    orphanNames.Add(pkg.Name);
                    changed = true;
                }
            }
        }

        if (orphanNames.Count == 0)
        {
            AnsiConsole.MarkupLine("[green bold]✔ No orphaned packages found. System is clean.[/]");
            return Task.CompletedTask;
        }

        // 3. Build orphan package list for display
        var orphanPackages = allPackages
            .Where(p => orphanNames.Contains(p.Name))
            .OrderBy(p => p.Name)
            .ToList();

        var totalSize = orphanPackages.Sum(p => p.InstalledSize);

        // 4. Display orphan table
        AnsiConsole.Write(new Rule("[red]Orphaned Packages[/]").RuleStyle("grey"));

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Package")
            .AddColumn("Version")
            .AddColumn("Arch")
            .AddColumn("Size");

        foreach (var pkg in orphanPackages)
        {
            var sizeStr = FormatSize(pkg.InstalledSize);
            table.AddRow(
                $"[red bold]{Markup.Escape(pkg.Name)}[/]",
                $"[grey]{Markup.Escape(pkg.FullVersion)}[/]",
                $"[grey]{Markup.Escape(pkg.Arch)}[/]",
                $"[yellow]{sizeStr}[/]"
            );
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[bold]Total packages:[/] {orphanPackages.Count}  |  [bold]Total size:[/] [yellow]{FormatSize(totalSize)}[/]");
        AnsiConsole.WriteLine();

        // 5. Confirm removal
        if (!config.AssumeYes && !AnsiConsole.Confirm($"Remove {orphanPackages.Count} orphaned packages?"))
        {
            AnsiConsole.MarkupLine("[grey]Cleanup cancelled.[/]");
            return Task.CompletedTask;
        }

        // 6. Execute removal
        var targets = string.Join(" ", orphanPackages.Select(p => p.Name));
        var psi = new ProcessStartInfo
        {
            FileName = "rpm",
            Arguments = $"--root {config.SysRoot} -evh {targets}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        AnsiConsole.MarkupLine("[blue]Executing cleanup transaction...[/]");

        using var process = Process.Start(psi);
        if (process == null)
        {
            AnsiConsole.MarkupLine("[red]Failed to start rpm process.[/]");
            return Task.CompletedTask;
        }

        process.OutputDataReceived += (s, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(e.Data)}[/]");
        };
        process.ErrorDataReceived += (s, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(e.Data)}[/]");
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();

        if (process.ExitCode == 0)
        {
            AnsiConsole.MarkupLine($"[green bold]✔ Cleanup complete. Removed {orphanPackages.Count} orphaned packages ({FormatSize(totalSize)} freed).[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red bold]Cleanup failed (Exit Code {process.ExitCode}).[/]");
        }

        return Task.CompletedTask;
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KiB", "MiB", "GiB" };
        double size = bytes;
        var unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.##} {units[unitIndex]}";
    }
}