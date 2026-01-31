using Aurora.Core.IO;
using Aurora.Core.Logic;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class RemoveCommand : ICommand
{
    public string Name => "remove";
    public string Description => "Remove a package";

    public Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("Usage: remove <package>");
        string pkgName = args[0];

        using var tx = new Transaction(config.DbPath);
        var pkg = tx.GetPackage(pkgName);

        if (pkg == null)
        {
            AnsiConsole.MarkupLine("[red]Package not found.[/]");
            return Task.CompletedTask;
        }

        if (!config.AssumeYes && !AnsiConsole.Confirm($"Are you sure you want to remove [bold]{pkgName}[/]?")) 
            return Task.CompletedTask;

        var scriptPath = Path.Combine(config.ScriptDir, $"{pkgName}.sh");
        bool hasScript = File.Exists(scriptPath);

        if (hasScript)
        {
            AnsiConsole.MarkupLine("[grey]Running pre-remove script...[/]");
            ScriptRunner.RunScript(scriptPath, "pre_remove", config.SysRoot);
        }

        AnsiConsole.Status().Start($"Removing {pkgName}...", ctx =>
        {
            foreach (var manifestPath in pkg.Files)
            {
                var physicalPath = PathHelper.GetPath(config.SysRoot, manifestPath);
                if (File.Exists(physicalPath))
                {
                    File.Delete(physicalPath);
                    var dir = Path.GetDirectoryName(physicalPath);
                    if (dir != null && !Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
            }
            tx.RemovePackage(pkgName);

            if (hasScript)
            {
                AnsiConsole.MarkupLine("[grey]Running post-remove script...[/]");
                ScriptRunner.RunScript(scriptPath, "post_remove", config.SysRoot);
                File.Delete(scriptPath);
            }
        });

        tx.Commit();
        AnsiConsole.MarkupLine($"[green]Removed {pkgName}.[/]");
        return Task.CompletedTask;
    }
}