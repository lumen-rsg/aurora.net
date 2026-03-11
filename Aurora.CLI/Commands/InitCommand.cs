using System.Diagnostics;
using Aurora.Core.IO;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class InitCommand : ICommand
{
    public string Name => "init";
    public string Description => "Initialize a new Aurora/RPM root environment";

    public Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        AnsiConsole.MarkupLine($"[blue]Initializing Root at:[/] {config.SysRoot}");

        // 1. Create a comprehensive skeleton. 
        // RPM needs more than just the DB dir to handle locks and lua scripts.
        var dirs = new[] { 
            "usr/lib/sysimage/rpm", 
            "var/lib/rpm",          
            "var/cache/aurora",     
            "var/lib/aurora",
            "etc/rpm",              // Needed for per-root macros
            "etc/yum.repos.d",
            "var/tmp",              
            "tmp",
            "var/lock/rpm",         // CRITICAL: Modern RPM lock location
            "run/lock"
        };

        foreach (var d in dirs)
        {
            string fullPath = PathHelper.GetPath(config.SysRoot, d);
            Directory.CreateDirectory(fullPath);
        }

        // 2. Symlink var/lib/rpm to usr/lib/sysimage/rpm (The Fedora Standard)
        // We do this BEFORE initdb so RPM can resolve it either way.
        string legacyDbPath = PathHelper.GetPath(config.SysRoot, "var/lib/rpm");
        if (Directory.Exists(legacyDbPath))
        {
            try { Directory.Delete(legacyDbPath); } catch { }
        }

        try 
        {
            // Use relative symlink: var/lib/rpm -> ../../usr/lib/sysimage/rpm
            File.CreateSymbolicLink(legacyDbPath, "../../usr/lib/sysimage/rpm");
            AnsiConsole.MarkupLine("[grey]Created legacy symlink compatibility...[/]");
        }
        catch { /* Fallback if symlink fails */ }

        // 3. Initialize the database
        AnsiConsole.MarkupLine("[blue]Initializing RPM database...[/]");
        
        // We REMOVE the manual --define for _dbpath. 
        // By providing both directories and the symlink, we let the host's RPM
        // find the path naturally via the --root flag.
        var psi = new ProcessStartInfo
        {
            FileName = "rpm",
            Arguments = $"--root \"{config.SysRoot}\" --initdb",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Ensure we don't inherit environment variables that point to the host's DB
        psi.EnvironmentVariables.Remove("RPM_CONFIGDIR");

        using var proc = Process.Start(psi);
        if (proc == null) return Task.CompletedTask;

        var err = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode == 0)
        {
            // Verify if the file was actually created
            string dbFile = Path.Combine(config.SysRoot, "usr/lib/sysimage/rpm/rpmdb.sqlite");
            if (File.Exists(dbFile))
                AnsiConsole.MarkupLine($"[green bold]✔ RPM database created at {dbFile}[/]");
            else
                AnsiConsole.MarkupLine("[yellow]! RPM reported success, but no sqlite file found in /usr/lib. Check /var/lib.[/]");

            AnsiConsole.MarkupLine($"[green bold]✔ Aurora root initialized.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Failed to initialize RPM db (Exit Code {proc.ExitCode}):[/]");
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(err)}[/]");
            
            // Helpful hint for Fedora 43 users
            if (err.Contains("lock"))
            {
                AnsiConsole.MarkupLine("[yellow]Hint:[/] RPM is having trouble with its lock file. Try running 'sudo rm -rf' on the target and starting fresh.");
            }
        }

        return Task.CompletedTask;
    }
}