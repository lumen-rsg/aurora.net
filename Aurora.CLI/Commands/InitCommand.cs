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

        // 1. Create essential modern directories
        var dirs = new[] { 
            "usr/lib/sysimage/rpm", // Modern RPM DB path
            "var/lib/aurora",       // Aurora metadata
            "var/cache/aurora",     // Aurora package cache
            "etc/yum.repos.d",      // Repository configs
            "usr/bin", 
            "usr/lib",
            "var/tmp",              // RPM requires this for transaction scripts
            "tmp"
        };

        foreach (var d in dirs)
        {
            string fullPath = PathHelper.GetPath(config.SysRoot, d);
            Directory.CreateDirectory(fullPath);
            AnsiConsole.MarkupLine($"[grey]Created dir:[/] {fullPath}");
        }

        // 2. Create the legacy RPM symlink
        // Modern Fedora relies on /var/lib/rpm being a symlink to ../../usr/lib/sysimage/rpm
        string varLib = PathHelper.GetPath(config.SysRoot, "var/lib");
        Directory.CreateDirectory(varLib);
        
        string rpmSymlink = Path.Combine(varLib, "rpm");
        if (!Directory.Exists(rpmSymlink) && !File.Exists(rpmSymlink))
        {
            try 
            {
                // .NET 6+ Native Symlink creation
                File.CreateSymbolicLink(rpmSymlink, "../../usr/lib/sysimage/rpm");
                AnsiConsole.MarkupLine($"[grey]Created symlink:[/] {rpmSymlink} -> ../../usr/lib/sysimage/rpm");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning:[/] Could not create /var/lib/rpm symlink: {ex.Message}");
            }
        }

        // 3. Initialize the RPM DB with strict sandboxed macros
        AnsiConsole.MarkupLine("[blue]Initializing RPM database...[/]");
        
        var psi = new ProcessStartInfo
        {
            FileName = "rpm",
            // We use quotes around SysRoot in case of spaces, and explicitly define the dbpath and backend
            Arguments = $"--root \"{config.SysRoot}\" --define \"_db_backend sqlite\" --define \"_dbpath /usr/lib/sysimage/rpm\" --initdb",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null) return Task.CompletedTask;

        var err = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode == 0)
        {
            AnsiConsole.MarkupLine($"[green bold]✔ RPM environment initialized successfully.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Failed to initialize RPM db (Exit Code {proc.ExitCode}):[/]");
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(err)}[/]");
        }

        return Task.CompletedTask;
    }
}