using Spectre.Console;

namespace Aurora.Core.Logic.Build;

public static class DependencyStub
{
    public static void CheckBuildDependencies(List<string> makedepends)
    {
        if (makedepends == null || makedepends.Count == 0) return;

        // Correct Spectre.Console API: AddColumn
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Dependency")
            .AddColumn("Status");

        foreach (var dep in makedepends)
        {
            if (string.IsNullOrWhiteSpace(dep)) continue;

            bool exists = TryFindOnHost(dep);
            string status = exists ? "[green]Found on Host[/]" : "[red]Missing[/]";
            table.AddRow(dep, status);
        }

        AnsiConsole.Write(
            new Panel(table)
                .Header("[bold cyan] Lumina Build Environment [/]")
                .Padding(1, 1)
                .BorderColor(Color.Blue)
        );

        AnsiConsole.MarkupLine("[yellow]ℹ Tip:[/] Lumina System mode is currently [bold]Inactive[/].");
        AnsiConsole.MarkupLine("[grey]Please ensure 'Missing' dependencies are installed via your host package manager.[/]");
        AnsiConsole.WriteLine();
    }

    private static bool TryFindOnHost(string name)
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator);
        if (paths == null) return false;

        foreach (var path in paths)
        {
            try
            {
                var fullPath = Path.Combine(path, name);
                if (File.Exists(fullPath)) return true;
            }
            catch { /* Skip invalid paths */ }
        }
        return false;
    }
}