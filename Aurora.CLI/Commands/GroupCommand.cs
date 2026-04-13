using System.Diagnostics;
using Aurora.Core.Logic;
using Aurora.Core.Models;
using Aurora.Core.Net;
using Aurora.Core.State;
using Aurora.Core.Logging;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class GroupCommand : ICommand
{
    public string Name => "group";
    public string Description => "Manage package groups (environments, collections)";

    public async Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        if (args.Length < 1)
        {
            PrintGroupHelp();
            return;
        }

        var subCommand = args[0].ToLowerInvariant();

        switch (subCommand)
        {
            case "list":
                await ListGroups(config, args.Skip(1).ToArray());
                break;
            case "info":
                await GroupInfo(config, args.Skip(1).ToArray());
                break;
            case "install":
                await GroupInstall(config, args.Skip(1).ToArray());
                break;
            case "remove":
                await GroupRemove(config, args.Skip(1).ToArray());
                break;
            default:
                AnsiConsole.MarkupLine($"[red]Unknown subcommand:[/] {subCommand}");
                PrintGroupHelp();
                break;
        }
    }

    private void PrintGroupHelp()
    {
        var table = new Table().Border(TableBorder.Rounded).AddColumn("Subcommand").AddColumn("Description");
        table.AddRow("[yellow]list[/]", "List all available package groups");
        table.AddRow("[yellow]info <group-id>[/]", "Show detailed info about a group and its packages");
        table.AddRow("[yellow]install <group-id>[/]", "Install all mandatory and default packages in a group");
        table.AddRow("[yellow]install <group-id> --with-optional[/]", "Install all packages including optional ones");
        table.AddRow("[yellow]remove <group-id>[/]", "Remove all packages in a group");
        AnsiConsole.Write(table);
    }

    /// <summary>
    ///     Lists all available package groups from synced repos.
    /// </summary>
    private async Task ListGroups(CliConfiguration config, string[] args)
    {
        var groups = RepoManager.LoadAllGroups(config.SysRoot);
        var categories = RepoManager.LoadAllCategories(config.SysRoot);
        var installedPkgs = RpmLocalDb.GetInstalledPackages(config.SysRoot);
        var installedNames = installedPkgs.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (groups.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No package groups found.[/] Run [bold]au sync[/] first.");
            return;
        }

        // Group groups by category
        var categoryLookup = categories
            .SelectMany(c => c.GroupIds.Select(gId => (GroupId: gId, Category: c)))
            .ToLookup(x => x.GroupId, x => x.Category);

        // Collect "uncategorized" groups
        var categorizedIds = new HashSet<string>(
            categories.SelectMany(c => c.GroupIds),
            StringComparer.OrdinalIgnoreCase);

        AnsiConsole.MarkupLine($"[bold]Available Package Groups ({groups.Count}):[/]\n");

        // Print by category
        foreach (var category in categories.OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name))
        {
            AnsiConsole.MarkupLine($"[bold blue]{category.Name}[/]");
            if (!string.IsNullOrEmpty(category.Description))
                AnsiConsole.MarkupLine($"[grey]  {Markup.Escape(category.Description)}[/]");

            var categoryGroups = groups
                .Where(g => category.GroupIds.Contains(g.Id, StringComparer.OrdinalIgnoreCase))
                .OrderBy(g => g.DisplayOrder).ThenBy(g => g.Name);

            foreach (var group in categoryGroups)
            {
                var installedCount = group.DefaultPackages.Count(p => installedNames.Contains(p.Name));
                var totalCount = group.DefaultPackages.Count();
                var status = installedCount == totalCount && totalCount > 0
                    ? "[green]✔[/]"
                    : installedCount > 0
                        ? "[yellow]◐[/]"
                        : "[grey]○[/]";

                AnsiConsole.MarkupLine($"  {status} [cyan]{group.Id}[/] [grey]({group.Name})[/]");

                if (group.Packages.Count > 0)
                {
                    var mandatory = group.Packages.Count(p => p.Type == GroupPackageType.Mandatory);
                    var def = group.Packages.Count(p => p.Type == GroupPackageType.Default);
                    var optional = group.Packages.Count(p => p.Type == GroupPackageType.Optional);
                    AnsiConsole.MarkupLine(
                        $"    [grey]{mandatory} mandatory, {def} default, {optional} optional[/]");
                }
            }

            AnsiConsole.WriteLine();
        }

        // Print uncategorized groups
        var uncategorized = groups
            .Where(g => !categorizedIds.Contains(g.Id))
            .OrderBy(g => g.Name)
            .ToList();

        if (uncategorized.Count > 0)
        {
            AnsiConsole.MarkupLine("[bold blue]Other Groups[/]");
            foreach (var group in uncategorized)
            {
                var installedCount = group.DefaultPackages.Count(p => installedNames.Contains(p.Name));
                var totalCount = group.DefaultPackages.Count();
                var status = installedCount == totalCount && totalCount > 0
                    ? "[green]✔[/]"
                    : installedCount > 0
                        ? "[yellow]◐[/]"
                        : "[grey]○[/]";

                AnsiConsole.MarkupLine($"  {status} [cyan]{group.Id}[/] [grey]({group.Name})[/]");
            }
        }

        AnsiConsole.MarkupLine("\n[grey]Legend: ✔ installed  ◐ partial  ○ not installed[/]");
    }

    /// <summary>
    ///     Shows detailed info about a specific group.
    /// </summary>
    private async Task GroupInfo(CliConfiguration config, string[] args)
    {
        if (args.Length < 1)
        {
            AnsiConsole.MarkupLine("[red]Usage:[/] group info <group-id>");
            return;
        }

        var groupId = args[0];
        var groups = RepoManager.LoadAllGroups(config.SysRoot);

        var group = groups.FirstOrDefault(g =>
            g.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));

        if (group == null)
        {
            // Try fuzzy matching
            var suggestions = groups
                .Where(g => g.Id.Contains(groupId, StringComparison.OrdinalIgnoreCase)
                            || g.Name.Contains(groupId, StringComparison.OrdinalIgnoreCase))
                .Take(5)
                .ToList();

            AnsiConsole.MarkupLine($"[red]Group '{groupId}' not found.[/]");
            if (suggestions.Count > 0)
            {
                AnsiConsole.MarkupLine("[yellow]Did you mean:[/]");
                foreach (var s in suggestions)
                    AnsiConsole.MarkupLine($"  [cyan]{s.Id}[/] [grey]({s.Name})[/]");
            }
            return;
        }

        var installedPkgs = RpmLocalDb.GetInstalledPackages(config.SysRoot);
        var installedNames = installedPkgs.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Group header
        AnsiConsole.Write(new Rule($"[cyan]{group.Name}[/]").RuleStyle("grey"));
        AnsiConsole.MarkupLine($"[bold]ID:[/]          {group.Id}");
        AnsiConsole.MarkupLine($"[bold]Repository:[/] {group.RepoId}");
        if (!string.IsNullOrEmpty(group.Description))
            AnsiConsole.MarkupLine($"[bold]Description:[/] {Markup.Escape(group.Description)}");
        AnsiConsole.MarkupLine($"[bold]Default:[/]     {(group.IsDefault ? "[green]Yes[/]" : "[grey]No[/]")}");
        AnsiConsole.MarkupLine($"[bold]Visible:[/]     {(group.Uservisible ? "[green]Yes[/]" : "[grey]No[/]")}");

        // Mandatory packages
        var mandatoryPkgs = group.Packages.Where(p => p.Type == GroupPackageType.Mandatory).ToList();
        if (mandatoryPkgs.Count > 0)
        {
            AnsiConsole.MarkupLine($"\n[bold red]Mandatory Packages ({mandatoryPkgs.Count}):[/]");
            foreach (var pkg in mandatoryPkgs)
            {
                var status = installedNames.Contains(pkg.Name) ? "[green]✔[/]" : "[grey]○[/]";
                AnsiConsole.MarkupLine($"  {status} {pkg.Name}");
            }
        }

        // Default packages
        var defaultPkgs = group.Packages.Where(p => p.Type == GroupPackageType.Default).ToList();
        if (defaultPkgs.Count > 0)
        {
            AnsiConsole.MarkupLine($"\n[bold yellow]Default Packages ({defaultPkgs.Count}):[/]");
            foreach (var pkg in defaultPkgs)
            {
                var status = installedNames.Contains(pkg.Name) ? "[green]✔[/]" : "[grey]○[/]";
                AnsiConsole.MarkupLine($"  {status} {pkg.Name}");
            }
        }

        // Optional packages
        var optionalPkgs = group.Packages.Where(p => p.Type == GroupPackageType.Optional).ToList();
        if (optionalPkgs.Count > 0)
        {
            AnsiConsole.MarkupLine($"\n[bold grey]Optional Packages ({optionalPkgs.Count}):[/]");
            foreach (var pkg in optionalPkgs)
            {
                var status = installedNames.Contains(pkg.Name) ? "[green]✔[/]" : "[grey]○[/]";
                AnsiConsole.MarkupLine($"  {status} {pkg.Name}");
            }
        }

        // Conditional packages
        var conditionalPkgs = group.Packages.Where(p => p.Type == GroupPackageType.Conditional).ToList();
        if (conditionalPkgs.Count > 0)
        {
            AnsiConsole.MarkupLine($"\n[bold grey]Conditional Packages ({conditionalPkgs.Count}):[/]");
            foreach (var pkg in conditionalPkgs)
            {
                var status = installedNames.Contains(pkg.Name) ? "[green]✔[/]" : "[grey]○[/]";
                AnsiConsole.MarkupLine($"  {status} {pkg.Name}");
            }
        }

        await Task.CompletedTask;
    }

    /// <summary>
    ///     Installs all mandatory and default packages in a group,
    ///     resolving dependencies through the existing DependencySolver.
    /// </summary>
    private async Task GroupInstall(CliConfiguration config, string[] args)
    {
        if (args.Length < 1)
        {
            AnsiConsole.MarkupLine("[red]Usage:[/] group install <group-id> [--with-optional]");
            return;
        }

        var groupId = args[0];
        var withOptional = args.Any(a => a == "--with-optional" || a == "--optional");

        var groups = RepoManager.LoadAllGroups(config.SysRoot);
        var group = groups.FirstOrDefault(g =>
            g.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));

        if (group == null)
        {
            AnsiConsole.MarkupLine($"[red]Group '{groupId}' not found.[/] Run [bold]au sync[/] first.");
            return;
        }

        // Collect target package names
        IEnumerable<GroupPackage> targetPkgs = withOptional
            ? group.Packages.Where(p => p.Type != GroupPackageType.Conditional)
            : group.DefaultPackages;

        var targetNames = targetPkgs.Select(p => p.Name).Distinct().ToList();

        if (targetNames.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]Group '{group.Name}' has no packages to install.[/]");
            return;
        }

        AnsiConsole.MarkupLine(
            $"[blue]Installing group [bold]{group.Name}[/] ({targetNames.Count} packages){(withOptional ? " [yellow]with optional[/]" : "")}...[/]");

        // Reuse the install logic — build the same pipeline as InstallCommand
        var installedPkgs = RpmLocalDb.GetInstalledPackages(config.SysRoot);
        var installedNames = installedPkgs.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetsToResolve = new List<string>();

        foreach (var name in targetNames)
        {
            if (!config.Force && installedNames.Contains(name))
            {
                AnsiConsole.MarkupLine($"[yellow]Skipping [bold]{name}[/]: already installed.[/]");
            }
            else
            {
                targetsToResolve.Add(name);
            }
        }

        if (targetsToResolve.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]Nothing to do. All group packages are already installed.[/]");
            return;
        }

        // Load available packages from repo DBs (parallel, optimized)
        var repoFiles = RepoLoader.DiscoverRepoDatabases(config.RepoDir);
        if (repoFiles.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No repository databases found. Run 'au sync' first.");
            return;
        }

        var availablePackages = await AnsiConsole.Status().StartAsync("Reading repositories...", _ =>
            Task.FromResult(RepoLoader.LoadAllPackages(config.RepoDir)));

        AnsiConsole.MarkupLine($"[grey]Loaded {availablePackages.Count} packages from {repoFiles.Length} repositories.[/]");

        // Resolve dependencies
        List<Package> plan;
        var sw = Stopwatch.StartNew();
        try
        {
            var solver = new DependencySolver(availablePackages, installedPkgs);
            int resolvedCount = 0;
            plan = AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots2)
                .Start("[cyan]Resolving dependencies...[/]", ctx =>
                {
                    var result = solver.Resolve(targetsToResolve, (count, name) =>
                    {
                        resolvedCount = count;
                        ctx.Status($"[cyan]Resolving dependencies...[/] [grey]({count} resolved)[/]");
                    });
                    return result;
                });

            sw.Stop();
            AnsiConsole.MarkupLine(
                $"[green bold]✔[/] Resolved [bold]{plan.Count}[/] packages in [grey]{sw.Elapsed.TotalSeconds:F1}s[/]");
        }
        catch (Exception ex)
        {
            sw.Stop();
            AnsiConsole.MarkupLine($"[red bold]Dependency Error:[/] {Markup.Escape(ex.Message)}");
            return;
        }

        // Confirm
        PrintTransactionSummary(plan, group.Name);
        if (!config.AssumeYes && !AnsiConsole.Confirm("Proceed with installation?")) return;

        // Download (Parallel)
        var repoMgr = new RepoManager(config.SysRoot) { SkipSignatureCheck = config.SkipGpg };
        var packagePaths = new string[plan.Count];
        var semaphore = new SemaphoreSlim(5);
        var rpmLogs = new List<string>();

        if (!Directory.Exists(config.CacheDir)) Directory.CreateDirectory(config.CacheDir);

        await AnsiConsole.Progress()
            .Columns(new ProgressColumn[]
            {
                new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new DownloadedColumn(),
                new SpinnerColumn()
            })
            .StartAsync(async ctx =>
            {
                var tasks = plan.Select(async (pkg, index) =>
                {
                    await semaphore.WaitAsync();
                    var task = ctx.AddTask($"[grey]{pkg.Name}[/]");
                    try
                    {
                        var path = await repoMgr.DownloadPackageAsync(pkg, config.CacheDir, (total, current) =>
                        {
                            if (total.HasValue)
                            {
                                task.MaxValue = total.Value;
                                task.Value = current;
                            }
                            else task.IsIndeterminate = true;
                        });

                        if (path == null) throw new FileNotFoundException($"Package {pkg.Name} not found.");
                        packagePaths[index] = path;
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]Download failed for {pkg.Name}:[/] {ex.Message}");
                        throw;
                    }
                    finally
                    {
                        task.StopTask();
                        semaphore.Release();
                    }
                });
                await Task.WhenAll(tasks);
            });

        // Execute
        AnsiConsole.Write(new Rule("[green]Installing[/]").RuleStyle("grey"));

        try
        {
            AnsiConsole.Status().Start("[cyan]Installing packages...[/]", ctx =>
            {
                SystemUpdater.ApplyUpdates(packagePaths, config.SysRoot, config.Force,
                    msg => rpmLogs.Add(msg));
            });
            AnsiConsole.MarkupLine($"\n[green bold]✔ Group '{group.Name}' installed successfully.[/]");

            // Record in history
            try
            {
                var historyEntries = plan.Select(p => new HistoryEntry
                {
                    Action = "group-install",
                    PackageName = p.Name,
                    Epoch = p.Epoch,
                    NewVersion = p.FullVersion,
                    Arch = p.Arch
                });
                await TransactionHistory.RecordTransactionAsync(config.DbPath, $"group-install:{group.Id}",
                    historyEntries);
            }
            catch (Exception histEx) { AuLogger.Error($"Failed to record history: {histEx.Message}"); }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red bold]Installation Failed:[/] {Markup.Escape(ex.Message)}");
            if (rpmLogs.Count > 0)
            {
                AnsiConsole.Write(new Rule("[yellow]RPM Output[/]").RuleStyle("yellow"));
                foreach (var log in rpmLogs)
                    AnsiConsole.MarkupLine($"[grey]{Markup.Escape(log)}[/]");
            }
        }
    }

    /// <summary>
    ///     Removes all packages belonging to a group that are currently installed.
    /// </summary>
    private async Task GroupRemove(CliConfiguration config, string[] args)
    {
        if (args.Length < 1)
        {
            AnsiConsole.MarkupLine("[red]Usage:[/] group remove <group-id>");
            return;
        }

        var groupId = args[0];
        var groups = RepoManager.LoadAllGroups(config.SysRoot);
        var group = groups.FirstOrDefault(g =>
            g.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));

        if (group == null)
        {
            AnsiConsole.MarkupLine($"[red]Group '{groupId}' not found.[/]");
            return;
        }

        var installedPkgs = RpmLocalDb.GetInstalledPackages(config.SysRoot);
        var installedNames = installedPkgs.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var pkgsToRemove = group.DefaultPackages
            .Select(p => p.Name)
            .Where(name => installedNames.Contains(name))
            .Distinct()
            .ToList();

        if (pkgsToRemove.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No packages from group '{group.Name}' are installed.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[blue]Removing {pkgsToRemove.Count} packages from group [bold]{group.Name}[/]...[/]");

        var table = new Table().Border(TableBorder.Rounded).AddColumn("Package");
        foreach (var name in pkgsToRemove)
            table.AddRow($"[red]{name}[/]");
        AnsiConsole.Write(table);

        if (!config.AssumeYes && !AnsiConsole.Confirm("Proceed with removal?")) return;

        var rpmLogs = new List<string>();
        try
        {
            var targets = string.Join(" ", pkgsToRemove);
            var psi = new ProcessStartInfo
            {
                FileName = "rpm",
                Arguments = $"--root {config.SysRoot} -evh {targets}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            AnsiConsole.MarkupLine("[blue]Executing removal transaction...[/]");

            using var process = Process.Start(psi);
            if (process == null)
            {
                AnsiConsole.MarkupLine("[red]Failed to start RPM process.[/]");
                return;
            }

            process.OutputDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) rpmLogs.Add(e.Data); };
            process.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) rpmLogs.Add($"[WARN] {e.Data}"); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();

            if (process.ExitCode == 0)
            {
                AnsiConsole.MarkupLine($"\n[green bold]✔ Group '{group.Name}' packages removed.[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red bold]Removal failed (Exit Code {process.ExitCode}).[/]");
                if (rpmLogs.Count > 0)
                {
                    AnsiConsole.Write(new Rule("[yellow]RPM Output[/]").RuleStyle("yellow"));
                    foreach (var log in rpmLogs)
                        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(log)}[/]");
                }
            }

            try
            {
                var historyEntries = pkgsToRemove.Select(name => new HistoryEntry
                {
                    Action = "group-remove",
                    PackageName = name
                });
                await TransactionHistory.RecordTransactionAsync(config.DbPath, $"group-remove:{group.Id}",
                    historyEntries);
            }
            catch (Exception histEx) { AuLogger.Error($"Failed to record history: {histEx.Message}"); }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red bold]Removal Failed:[/] {Markup.Escape(ex.Message)}");
            if (rpmLogs.Count > 0)
            {
                AnsiConsole.Write(new Rule("[yellow]RPM Output[/]").RuleStyle("yellow"));
                foreach (var log in rpmLogs)
                    AnsiConsole.MarkupLine($"[grey]{Markup.Escape(log)}[/]");
            }
        }
    }

    private void PrintTransactionSummary(List<Package> plan, string groupName)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Package");
        table.AddColumn("Version");
        table.AddColumn("Size");
        long totalSize = 0;
        foreach (var p in plan)
        {
            table.AddRow($"[cyan]{p.Name}[/]", $"[grey]{p.FullVersion}[/]", FormatBytes(p.Size));
            totalSize += p.Size;
        }
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"Group: [bold]{groupName}[/] | Total Download Size: [bold green]{FormatBytes(totalSize)}[/]\n");
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffix = { "B", "KB", "MB", "GB", "TB" };
        int i; double dblSByte = bytes;
        for (i = 0; i < suffix.Length && bytes >= 1024; i++, bytes /= 1024) dblSByte = bytes / 1024.0;
        return string.Format("{0:0.00} {1}", dblSByte, suffix[i]);
    }
}