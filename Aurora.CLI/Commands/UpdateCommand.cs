using Aurora.Core.Logic;
using Aurora.Core.Models;
using Aurora.Core.Parsing; // Needed for Package list
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class UpdateCommand : ICommand
{
    public string Name => "update";
    public string Description => "Update system";

    public Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        // 1. Check if Repo Directory exists
        if (!Directory.Exists(config.RepoDir))
        {
            AnsiConsole.MarkupLine($"[red]No repository directory found at {config.RepoDir}.[/]");
            AnsiConsole.MarkupLine("[yellow]Run 'sync' first.[/]");
            return Task.CompletedTask;
        }

        // 2. Load ALL repository metadata files
        var repoFiles = Directory.GetFiles(config.RepoDir, "*.aurepo");
        if (repoFiles.Length == 0)
        {
            AnsiConsole.MarkupLine($"[red]No repository databases found in {config.RepoDir}.[/]");
            AnsiConsole.MarkupLine("[yellow]Run 'sync' first.[/]");
            return Task.CompletedTask;
        }

        AnsiConsole.MarkupLine($"[blue]Reading repository state from {repoFiles.Length} source(s)...[/]");

        var repoPackages = new List<Package>();
        foreach (var file in repoFiles)
        {
            try 
            {
                var content = File.ReadAllText(file);
                var parsedRepo = RepoParser.Parse(content);
                repoPackages.AddRange(parsedRepo.Packages);
            } 
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: Failed to parse {Path.GetFileName(file)}: {ex.Message}[/]");
            }
        }

        // 3. Start Transaction & Update
        try
        {
            using var tx = new Transaction(config.DbPath);
            var updater = new SystemUpdater(tx, repoPackages);

            updater.PerformUpdate(config.SysRoot, (msg) => AnsiConsole.MarkupLine($"[grey]{msg}[/]"));
            
            tx.Commit();
            AnsiConsole.MarkupLine("[green bold]System update completed successfully.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red bold]Update Failed:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.MarkupLine("[yellow]Transaction rolled back.[/]");
        }
        
        return Task.CompletedTask;
    }
}