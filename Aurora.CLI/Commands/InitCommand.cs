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
        // Create the essential modern filesystem skeleton
        var dirs = new[] { 
            "usr/lib/sysimage/rpm", // Modern RPM DB path (Fedora 36+)
            "var/lib/rpm",          // Legacy RPM DB path
            "var/lib/aurora",       // Aurora metadata
            "var/cache/aurora",     // Aurora package cache
            "etc/yum.repos.d",      // Repository configs
            "usr/bin", 
            "usr/lib",
            "var/tmp",              // Required by RPM for Lua/transaction locks
            "tmp"                   // Required by some %pre/%post scripts
        };

        foreach (var d in dirs)
        {
            Directory.CreateDirectory(PathHelper.GetPath(config.SysRoot, d));
        }

        AnsiConsole.MarkupLine("[blue]Initializing RPM database...[/]");
        var psi = new ProcessStartInfo
        {
            FileName = "rpm",
            Arguments = $"--root {config.SysRoot} --initdb",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        proc?.WaitForExit();

        if (proc?.ExitCode == 0)
        {
            AnsiConsole.MarkupLine($"[green]✔ Initialized Aurora root at {config.SysRoot}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Failed to initialize RPM db:[/] {proc?.StandardError.ReadToEnd()}");
        }

        return Task.CompletedTask;
    }
}