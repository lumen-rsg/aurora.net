using Aurora.Core.Logic;
using Aurora.Core.Models;
using Aurora.Core.State;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class SearchCommand : ICommand
{
    public string Name => "search";
    public string Description => "Search for packages in repositories";

    public async Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        if (args.Length < 1)
        {
            AnsiConsole.MarkupLine("[red]Usage:[/] au search <query>");
            return;
        }

        string query = args[0].ToLowerInvariant();

        // 1. Check for synced repository databases
        var repoFiles = Directory.GetFiles(config.RepoDir, "*.sqlite");
        if (repoFiles.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No repository databases found. Run 'au sync' first.");
            return;
        }

        // 2. Load all packages from all repos
        var allPackages = new List<(Package Pkg, string RepoId)>();
        int loadedCount = 0;

        await AnsiConsole.Status().StartAsync("Reading repositories...", async ctx =>
        {
            foreach (var dbFile in repoFiles)
            {
                try
                {
                    using var db = new RpmRepoDb(dbFile);
                    string repoId = Path.GetFileNameWithoutExtension(dbFile);
                    var pkgs = db.GetAllPackages(repoId);
                    foreach (var p in pkgs) allPackages.Add((p, repoId));
                    loadedCount += pkgs.Count;
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error reading {Path.GetFileName(dbFile)}:[/] {ex.Message}");
                }
            }
        });

        AnsiConsole.MarkupLine($"[grey]Loaded {loadedCount} packages from {repoFiles.Length} repositories.[/]");

        // 3. Multi-tier search with ranking
        const int fuzzyThreshold = 3; // max Levenshtein distance for fuzzy matches

        var results = new List<(Package Pkg, string RepoId, int Rank)>();

        foreach (var (pkg, repoId) in allPackages)
        {
            string nameLower = pkg.Name.ToLowerInvariant();
            int rank;

            if (nameLower == query)
                rank = 0; // Exact match — highest
            else if (nameLower.StartsWith(query))
                rank = 1; // Starts with
            else if (nameLower.Contains(query))
                rank = 2; // Contains
            else
            {
                int dist = FuzzyMatcher.LevenshteinDistance(nameLower, query);
                if (dist <= fuzzyThreshold)
                    rank = 3 + dist; // Fuzzy — lower rank, further = worse
                else
                    continue; // No match
            }

            results.Add((pkg, repoId, rank));
        }

        // 4. Deduplicate by name (keep best rank, then highest version)
        var deduped = results
            .GroupBy(r => r.Pkg.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(r => r.Rank).ThenByDescending(r => r.Pkg.FullVersion).First())
            .ToList();

        // 5. Sort: by rank, then alphabetically
        deduped.Sort((a, b) =>
        {
            int cmp = a.Rank.CompareTo(b.Rank);
            return cmp != 0 ? cmp : string.Compare(a.Pkg.Name, b.Pkg.Name, StringComparison.OrdinalIgnoreCase);
        });

        // 6. Display results
        if (deduped.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No packages found matching '[bold]{Markup.Escape(query)}[/]'.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Name");
        table.AddColumn("Version");
        table.AddColumn("Arch");
        table.AddColumn("Repository");
        table.AddColumn("Size");

        foreach (var (pkg, repoId, rank) in deduped)
        {
            string nameMarkup = rank switch
            {
                0 => $"[bold green]{Markup.Escape(pkg.Name)}[/]",  // exact
                1 => $"[cyan]{Markup.Escape(pkg.Name)}[/]",         // starts with
                2 => $"[white]{Markup.Escape(pkg.Name)}[/]",        // contains
                _ => $"[grey]{Markup.Escape(pkg.Name)}[/]"          // fuzzy
            };

            table.AddRow(
                nameMarkup,
                $"[grey]{Markup.Escape(pkg.FullVersion)}[/]",
                pkg.Arch,
                Markup.Escape(repoId),
                FormatBytes(pkg.Size)
            );
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[bold]Found {deduped.Count} matching package(s).[/]");
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffix = { "B", "KB", "MB", "GB", "TB" };
        int i;
        double dblSByte = bytes;
        for (i = 0; i < suffix.Length && bytes >= 1024; i++, bytes /= 1024)
            dblSByte = bytes / 1024.0;
        return string.Format("{0:0.00} {1}", dblSByte, suffix[i]);
    }
}