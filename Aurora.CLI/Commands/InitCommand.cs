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
        // 1. Ensure we have the absolute host path for the SysRoot
        string absoluteRoot = Path.GetFullPath(config.SysRoot);
        AnsiConsole.MarkupLine($"[blue]Initializing Root at:[/] {absoluteRoot}");

        // 2. Create the exact modern Fedora skeleton
        // We include /run and /var/lock to satisfy RPM's locking sub-systems
        var dirs = new[] { 
            "usr/lib/sysimage/rpm",
            "etc/yum.repos.d",
            "var/lib/aurora",
            "var/cache/aurora",
            "var/tmp",
            "tmp",
            "run/lock/rpm"
        };

        foreach (var d in dirs)
        {
            Directory.CreateDirectory(Path.Combine(absoluteRoot, d));
        }

        // 3. Create the legacy symlink
        // var/lib/rpm -> ../../usr/lib/sysimage/rpm
        string legacyLink = Path.Combine(absoluteRoot, "var/lib/rpm");
        if (!Directory.Exists(legacyLink) && !File.Exists(legacyLink))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(legacyLink)!);
            File.CreateSymbolicLink(legacyLink, "../../usr/lib/sysimage/rpm");
        }

        // 4. Initialize RPM DB using Absolute Host Paths
        // By using --dbpath [ABSOLUTE_HOST_PATH], we bypass the internal '--root' logic
        // which often fails to create locks on blank directories.
        string absoluteHostDbPath = Path.Combine(absoluteRoot, "usr/lib/sysimage/rpm");

        AnsiConsole.MarkupLine("[blue]Initializing RPM database (Direct Path)...[/]");
        
        var psi = new ProcessStartInfo
        {
            FileName = "rpm",
            // We force the sqlite backend and point directly to the host path
            Arguments = $"--define \"_db_backend sqlite\" --dbpath \"{absoluteHostDbPath}\" --initdb",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Clean the environment to prevent host-specific RPM macros from interfering
        psi.EnvironmentVariables.Remove("RPM_CONFIGDIR");
        psi.EnvironmentVariables.Remove("RPM_ETCCONFIGDIR");

        using var proc = Process.Start(psi);
        if (proc == null) return Task.CompletedTask;

        var err = proc.StandardError.ReadToEnd();
        var outMsg = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode == 0)
        {
            // Success check
            string dbFile = Path.Combine(absoluteHostDbPath, "rpmdb.sqlite");
            if (File.Exists(dbFile))
            {
                AnsiConsole.MarkupLine($"[green bold]✔ RPM SQLite database created successfully.[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]! RPM reported success, but rpmdb.sqlite is missing. Check /var/lib/rpm.[/]");
            }
            
            AnsiConsole.MarkupLine($"[green bold]✔ Aurora root ready.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Failed to initialize RPM db (Exit Code {proc.ExitCode}):[/]");
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(err)}[/]");
            
            if (err.Contains("lock") && OperatingSystem.IsLinux())
            {
                AnsiConsole.MarkupLine("[yellow]SELinux Hint:[/] If you are on Fedora host, run: [blue]sudo setenforce 0[/] and try again.");
            }
        }

        return Task.CompletedTask;
    }
}