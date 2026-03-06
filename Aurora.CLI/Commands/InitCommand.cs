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
        // Essential directories for RPM and Aurora
        var dirs = new[] { 
            "var/lib/rpm", 
            "var/lib/aurora", 
            "var/cache/aurora", 
            "etc/yum.repos.d",
            "usr/bin", 
            "usr/lib" 
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