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

        // 1. Load all packages from all repos (parallel, optimized)
        var repoFiles = RepoLoader.DiscoverRepoDatabases(config.RepoDir);
        if (repoFiles.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No repository databases found. Run 'au sync' first.");
            return;
        }

        var allPackages = await AnsiConsole.Status().StartAsync("Reading repositories...", _ =>
            Task.FromResult(RepoLoader.LoadAllPackages(config.RepoDir)));

        AnsiConsole.MarkupLine($"[grey]Loaded {allPackages.Count} packages from {repoFiles.Length} repositories.[/]");

        // 3. Multi-tier search with token-based ranking
        const int fuzzyThreshold = 3; // max Levenshtein distance for fuzzy matches

        var results = new List<(Package Pkg, int Rank)>();

        foreach (var pkg in allPackages)
        {
            string nameLower = pkg.Name.ToLowerInvariant();
            int rank;

            if (nameLower == query)
            {
                rank = 0; // Exact name match — highest
            }
            else if (nameLower.StartsWith(query))
            {
                rank = 1; // Name starts with query (e.g. "gnome-shell")
            }
            else
            {
                // Split into hyphen-delimited tokens for smarter matching
                string[] tokens = nameLower.Split('-');

                if (tokens.Any(t => t == query))
                    rank = 2; // A token exactly equals the query
                else if (tokens.Any(t => t.StartsWith(query)))
                    rank = 3; // A token starts with the query
                else if (tokens.Any(t => t.Contains(query)))
                    rank = 4; // A token contains the query
                else
                {
                    // Fuzzy match against individual tokens (not the whole name)
                    int minDist = tokens
                        .Select(t => FuzzyMatcher.LevenshteinDistance(t, query))
                        .Min();

                    if (minDist <= fuzzyThreshold)
                        rank = 5 + minDist; // Fuzzy — lower rank, further = worse
                    else
                        continue; // No match
                }
            }

            results.Add((pkg, rank));
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

        foreach (var (pkg, rank) in deduped)
        {
            string nameMarkup = rank switch
            {
                0 => $"[bold green]{Markup.Escape(pkg.Name)}[/]",  // exact name
                1 => $"[cyan]{Markup.Escape(pkg.Name)}[/]",         // name starts with
                2 => $"[green]{Markup.Escape(pkg.Name)}[/]",        // token exact match
                3 => $"[white]{Markup.Escape(pkg.Name)}[/]",        // token starts with
                4 => $"[yellow]{Markup.Escape(pkg.Name)}[/]",       // token contains
                _ => $"[grey]{Markup.Escape(pkg.Name)}[/]"          // fuzzy
            };

            table.AddRow(
                nameMarkup,
                $"[grey]{Markup.Escape(pkg.FullVersion)}[/]",
                pkg.Arch,
                Markup.Escape(pkg.RepositoryId),
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