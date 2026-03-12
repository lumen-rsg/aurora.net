using Aurora.Core.Models;
using Aurora.Core.State;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class InfoCommand : ICommand
{
    public string Name => "info";
    public string Description => "Show detailed information for a package in the repositories";

    public async Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        if (args.Length < 1)
        {
            AnsiConsole.MarkupLine("[red]Usage:[/] au info <package_name>");
            return;
        }

        string targetName = args[0];
        Package? foundPkg = null;
        string repoName = "";

        // 1. Search across all synced repositories
        var repoFiles = Directory.GetFiles(config.RepoDir, "*.sqlite");
        if (repoFiles.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No repository databases found. Run 'au sync' first.");
            return;
        }

        foreach (var dbFile in repoFiles)
        {
            using var db = new RpmRepoDb(dbFile);
            string repoId = Path.GetFileNameWithoutExtension(dbFile); 
            var pkgs = db.GetAllPackages(repoId);
            foundPkg = pkgs.FirstOrDefault(p => p.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));
            
            if (foundPkg != null)
            {
                repoName = Path.GetFileNameWithoutExtension(dbFile);
                break;
            }
        }

        if (foundPkg == null)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Package [bold]{targetName}[/] not found in any repository.");
            return;
        }

        // 2. Render Beautiful Info Panel
        var table = new Table().Border(TableBorder.None).HideHeaders().AddColumn("Key").AddColumn("Value");

        table.AddRow("[blue]Name[/]", $"[bold]{foundPkg.Name}[/]");
        table.AddRow("[blue]Version[/]", foundPkg.FullVersion);
        table.AddRow("[blue]Arch[/]", foundPkg.Arch);
        table.AddRow("[blue]Repository[/]", repoName);
        table.AddRow("[blue]License[/]", foundPkg.License);
        table.AddRow("[blue]URL[/]", $"[link]{foundPkg.Url}[/]");
        table.AddRow("[blue]Download Size[/]", FormatBytes(foundPkg.Size));

        // Description Section
        var descPanel = new Panel(Markup.Escape(foundPkg.Description))
        {
            Header = new PanelHeader(" Description "),
            Border = BoxBorder.Rounded,
            Padding = new Padding(1, 0, 1, 0)
        };

        // Relationships (Requires / Provides)
        var relGrid = new Grid().AddColumn().AddColumn();
        
        var reqList = foundPkg.Requires.Count > 0 
            ? string.Join("\n", foundPkg.Requires.Take(15).Select(r => $"[grey]-[/] {Markup.Escape(r)}")) 
            : "[grey]None[/]";
        if (foundPkg.Requires.Count > 15) reqList += $"\n[grey]... and {foundPkg.Requires.Count - 15} more[/]";

        var provList = foundPkg.Provides.Count > 0 
            ? string.Join("\n", foundPkg.Provides.Take(15).Select(p => $"[yellow]-[/] {Markup.Escape(p)}")) 
            : "[grey]None[/]";
        if (foundPkg.Provides.Count > 15) provList += $"\n[grey]... and {foundPkg.Provides.Count - 15} more[/]";

        relGrid.AddRow(
            new Panel(reqList) { Header = new PanelHeader(" Requires "), Border = BoxBorder.Rounded },
            new Panel(provList) { Header = new PanelHeader(" Provides "), Border = BoxBorder.Rounded }
        );

        // Layout everything
        AnsiConsole.Write(new Rule($"[bold yellow]Package Information:[/] {foundPkg.Nevra}").RuleStyle("grey"));
        AnsiConsole.Write(table);
        AnsiConsole.Write(descPanel);
        AnsiConsole.Write(relGrid);
    }

    private string FormatBytes(long bytes)
    {
        string[] suffix = { "B", "KB", "MB", "GB", "TB" };
        int i; double dblSByte = bytes;
        for (i = 0; i < suffix.Length && bytes >= 1024; i++, bytes /= 1024) dblSByte = bytes / 1024.0;
        return string.Format("{0:0.00} {1}", dblSByte, suffix[i]);
    }
}