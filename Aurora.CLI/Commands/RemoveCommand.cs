using Aurora.Core.IO;
using Aurora.Core.Logic;
using Aurora.Core.Logic.Hooks; // Hook Logic
using Aurora.Core.Models;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class RemoveCommand : ICommand
{
    public string Name => "remove";
    public string Description => "Remove a package and its files";

    public async Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("Usage: remove <package>");
        string pkgName = args[0];

        // 1. Open Transaction
        using var tx = new Transaction(config.DbPath);
        
        try
        {
            // 2. Lookup Package
            var pkg = tx.GetPackage(pkgName);

            if (pkg == null)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Package [bold]{pkgName}[/] is not installed.");
                return;
            }

            // TODO: In the future, check reverse dependencies here.
            // e.g. "Cannot remove 'glibc' because 'bash' depends on it."
            
            // 3. User Confirmation
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Removing");
            table.AddColumn("Version");
            table.AddRow($"[red]{pkg.Name}[/]", pkg.Version);
            AnsiConsole.Write(table);

            if (!config.AssumeYes && !AnsiConsole.Confirm($"Are you sure you want to remove [bold]{pkgName}[/]?")) 
            {
                AnsiConsole.MarkupLine("[yellow]Operation cancelled.[/]");
                return; 
            }

            // 4. Init Hooks
            var hookEngine = new HookEngine(config.SysRoot);
            var targetList = new List<Package> { pkg };

            // 5. Pre-Transaction Hooks
            AnsiConsole.Write(new Rule("[grey]Pre-Remove Hooks[/]").RuleStyle("grey"));
            await hookEngine.RunHooksAsync(HookWhen.PreTransaction, targetList, TriggerOperation.Remove);

            // 6. Legacy Script Support (Pre-Remove)
            // Some older packages might use shell scripts instead of hooks
            var scriptPath = Path.Combine(config.ScriptDir, $"{pkgName}.sh");
            bool hasScript = File.Exists(scriptPath);

            if (hasScript)
            {
                AnsiConsole.MarkupLine("[grey]Running legacy pre-remove script...[/]");
                ScriptRunner.RunScript(scriptPath, "pre_remove", config.SysRoot);
            }

            // 7. Physical Removal
            AnsiConsole.Status().Start($"Removing {pkgName}...", ctx =>
            {
                // Delete files listed in the database for this package
                foreach (var manifestPath in pkg.Files)
                {
                    var physicalPath = PathHelper.GetPath(config.SysRoot, manifestPath);
                    if (File.Exists(physicalPath))
                    {
                        File.Delete(physicalPath);
                        
                        // Clean up empty parent directory
                        var dir = Path.GetDirectoryName(physicalPath);
                        if (dir != null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                        {
                            try { Directory.Delete(dir); } catch { }
                        }
                    }
                    else if (Directory.Exists(physicalPath))
                    {
                        // If it's a directory owned by the package, remove if empty
                        if (!Directory.EnumerateFileSystemEntries(physicalPath).Any())
                            try { Directory.Delete(physicalPath); } catch { }
                    }
                }
                
                // Remove from DB
                tx.RemovePackage(pkgName);
            });

            // 8. Legacy Script Support (Post-Remove)
            if (hasScript)
            {
                AnsiConsole.MarkupLine("[grey]Running legacy post-remove script...[/]");
                ScriptRunner.RunScript(scriptPath, "post_remove", config.SysRoot);
                try { File.Delete(scriptPath); } catch { }
            }

            // 9. Commit
            tx.Commit();

            // 10. Post-Transaction Hooks
            AnsiConsole.Write(new Rule("[grey]Post-Remove Hooks[/]").RuleStyle("grey"));
            await hookEngine.RunHooksAsync(HookWhen.PostTransaction, targetList, TriggerOperation.Remove);

            AnsiConsole.MarkupLine($"[green]✔ Successfully removed {pkgName}.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red bold]Removal Failed:[/] {Markup.Escape(ex.Message)}");
            // Transaction auto-rollback handles DB consistency, 
            // but deleted files (physical) are harder to rollback without a backup cache.
            // Aurora's current transaction model is primarily for DB consistency + crash recovery logic.
        }
    }
}