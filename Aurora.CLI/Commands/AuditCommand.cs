using Aurora.Core.Logic;
using Aurora.Core.Models;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class AuditCommand : ICommand
{
    public string Name => "audit";
    public string Description => "Check system health";

    public Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        AnsiConsole.MarkupLine("[blue]Auditing system health...[/]");
        using var tx = new Transaction(config.DbPath);
        var brokenList = tx.GetBrokenPackages();

        if (brokenList.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]System is healthy.[/]");
            return Task.CompletedTask;
        }

        AnsiConsole.MarkupLine($"Found [red bold]{brokenList.Count}[/] broken packages.");
        
        var table = new Table().AddColumn("Package").AddColumn("Issues").AddColumn("Status");
        int healedCount = 0;
        
        var allInstalled = tx.GetAllPackages();
        var installedMap = allInstalled.ToDictionary(p => p.Name, p => p);

        foreach (var pkg in brokenList)
        {
            var issues = new List<string>();

            // 1. Check Missing Dependencies
            foreach (var dep in pkg.Depends)
            {
                if (!installedMap.ContainsKey(dep))
                {
                    issues.Add($"Missing dep: {dep}");
                }
            }

            // 2. Check Conflicts (Forward)
            // Does this package declare a conflict with an installed package?
            foreach (var c in pkg.Conflicts)
            {
                if (installedMap.ContainsKey(c))
                {
                    issues.Add($"Conflicts with {c}");
                }
            }

            // 3. Check Conflicts (Reverse)
            // Does an installed package declare a conflict with this one?
            foreach (var other in allInstalled)
            {
                if (other.Name == pkg.Name) continue; // Skip self
                if (other.Conflicts.Contains(pkg.Name))
                {
                    issues.Add($"Conflict from {other.Name}");
                }
            }

            if (issues.Count == 0)
            {
                tx.MarkPackageHealthy(pkg.Name);
                table.AddRow(pkg.Name, "[grey]None[/]", "[green bold]HEALED[/]");
                healedCount++;
            }
            else
            {
                var issueText = string.Join(", ", issues);
                table.AddRow(pkg.Name, $"[red]{issueText}[/]", "[red]STILL BROKEN[/]");
            }
        }

        AnsiConsole.Write(table);
        
        if (healedCount > 0)
        {
            tx.Commit();
            AnsiConsole.MarkupLine($"[green]Successfully healed {healedCount} packages.[/]");
        }
        
        return Task.CompletedTask;
    }
}