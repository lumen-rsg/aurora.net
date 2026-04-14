using System.Diagnostics;
using System.Globalization;
using Aurora.Core.Logic;
using Aurora.Core.Logging;
using Aurora.Core.Models;
using Aurora.Core.Net;
using Aurora.Core.State;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class HistoryCommand : ICommand
{
    public string Name => "history";
    public string Description => "View transaction history and perform rollbacks";

    public async Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        var dbPath = config.DbPath;

        // Parse sub-arguments
        bool listMode = args.Contains("--list") || args.Contains("-l");
        long? viewId = null;

        foreach (var arg in args)
        {
            if (arg.StartsWith("--view=") && long.TryParse(arg["--view=".Length..], out var vid))
                viewId = vid;
            else if (arg.StartsWith("--rollback=") && long.TryParse(arg["--rollback=".Length..], out var rid))
            {
                await PerformRollbackAsync(config, rid);
                return;
            }
        }

        if (viewId.HasValue)
        {
            await ShowTransactionDetailAsync(dbPath, viewId.Value);
            return;
        }

        if (listMode)
        {
            await ShowListModeAsync(dbPath);
            return;
        }

        // Interactive TUI mode
        await RunInteractiveTuiAsync(config, dbPath);
    }

    // ──────────────────────────────────────────────────────────────
    // Interactive TUI
    // ──────────────────────────────────────────────────────────────

    private async Task RunInteractiveTuiAsync(CliConfiguration config, string dbPath)
    {
        while (true)
        {
            AnsiConsole.Clear();

            // Header
            AnsiConsole.Write(new Rule("[cyan bold]aurora — Transaction History[/]").RuleStyle("cyan").DoubleBorder());
            AnsiConsole.WriteLine();

            // Fetch history
            var transactions = await AnsiConsole.Status()
                .StartAsync("Loading history...", _ => TransactionHistory.GetHistoryAsync(dbPath, 100, 0));

            if (transactions.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No transaction history found.[/]");
                AnsiConsole.MarkupLine("[grey]Transactions are recorded when you install, remove, or update packages.[/]");
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[dim]Press any key to exit...[/]");
                Console.ReadKey();
                return;
            }

            // Summary bar
            var totalCount = await TransactionHistory.GetTransactionCountAsync(dbPath);
            var installs = transactions.Count(t => t.Type == "install");
            var removes = transactions.Count(t => t.Type == "remove");
            var updates = transactions.Count(t => t.Type == "update");

            var summaryPanel = new Panel(
                new Markup(
                    $"[bold]Total:[] [white]{totalCount}[/]   " +
                    $"[green bold]Install:[/] [white]{installs}[/]   " +
                    $"[red bold]Remove:[/] [white]{removes}[/]   " +
                    $"[blue bold]Update:[/] [white]{updates}[/]"))
            {
                Border = BoxBorder.Rounded,
                BorderStyle = Style.Parse("grey")
            };
            AnsiConsole.Write(summaryPanel);
            AnsiConsole.WriteLine();

            // Transaction table
            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Grey)
                .AddColumn(new TableColumn("[bold]#[/]").Width(6).RightAligned())
                .AddColumn(new TableColumn("[bold]Time[/]").Width(20))
                .AddColumn(new TableColumn("[bold]Type[/]").Width(10))
                .AddColumn(new TableColumn("[bold]Packages[/]"))
                .AddColumn(new TableColumn("[bold]Count[/]").Width(8).RightAligned());

            foreach (var tx in transactions)
            {
                var (typeMarkup, icon) = GetTypeStyle(tx.Type);

                var timeStr = tx.Timestamp.ToLocalTime().ToString("MMM dd, HH:mm:ss", CultureInfo.InvariantCulture);

                // Show first 3 package names, then "+N more"
                var pkgNames = tx.Entries.Select(e => e.PackageName).ToList();
                string pkgDisplay;
                if (pkgNames.Count == 0)
                {
                    pkgDisplay = "[grey](empty)[/]";
                }
                else if (pkgNames.Count <= 4)
                {
                    pkgDisplay = string.Join("[grey], [/]", pkgNames.Select(p => $"[white]{Markup.Escape(p)}[/]"));
                }
                else
                {
                    var shown = pkgNames.Take(3).Select(p => $"[white]{Markup.Escape(p)}[/]");
                    pkgDisplay = string.Join("[grey], [/]", shown) + $" [dim]...+{pkgNames.Count - 3} more[/]";
                }

                table.AddRow(
                    $"[yellow]{tx.Id}[/]",
                    $"[grey]{timeStr}[/]",
                    $"{icon} {typeMarkup}",
                    pkgDisplay,
                    $"[white]{tx.Entries.Count}[/]"
                );
            }

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();

            // Navigation prompt
            var choices = new List<string>();
            choices.Add("📋  View transaction by ID");
            choices.Add("⏪  Rollback to a transaction");
            choices.Add("🔄  Refresh");
            choices.Add("❌  Exit");

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold cyan]What would you like to do?[/]")
                    .PageSize(10)
                    .HighlightStyle(Style.Parse("cyan bold"))
                    .AddChoices(choices));

            if (choice.StartsWith("❌"))
            {
                AnsiConsole.Clear();
                AnsiConsole.MarkupLine("[dim]Goodbye.[/]");
                return;
            }

            if (choice.StartsWith("🔄"))
            {
                continue; // refresh
            }

            if (choice.StartsWith("📋"))
            {
                var idStr = AnsiConsole.Prompt(
                    new TextPrompt<string>("[cyan]Enter transaction ID to view:[/]")
                        .ValidationErrorMessage("[red]Invalid ID.[/]"));
                if (long.TryParse(idStr, out var viewId))
                {
                    AnsiConsole.Clear();
                    await ShowTransactionDetailAsync(dbPath, viewId);
                }
                else
                {
                    AnsiConsole.MarkupLine("[red]Invalid transaction ID.[/]");
                }

                AnsiConsole.MarkupLine("\n[dim]Press any key to continue...[/]");
                Console.ReadKey();
                continue;
            }

            if (choice.StartsWith("⏪"))
            {
                var idStr = AnsiConsole.Prompt(
                    new TextPrompt<string>("[cyan]Enter transaction ID to rollback to:[/]")
                        .ValidationErrorMessage("[red]Invalid ID.[/]"));
                if (long.TryParse(idStr, out var rollbackId))
                {
                    AnsiConsole.Clear();
                    await PerformRollbackAsync(config, rollbackId);
                }
                else
                {
                    AnsiConsole.MarkupLine("[red]Invalid transaction ID.[/]");
                }

                AnsiConsole.MarkupLine("\n[dim]Press any key to continue...[/]");
                Console.ReadKey();
                continue;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Transaction Detail View
    // ──────────────────────────────────────────────────────────────

    private async Task ShowTransactionDetailAsync(string dbPath, long transactionId)
    {
        var tx = await AnsiConsole.Status()
            .StartAsync("Loading transaction...", _ => TransactionHistory.GetTransactionAsync(dbPath, transactionId));

        if (tx == null)
        {
            AnsiConsole.MarkupLine($"[red]Transaction #{transactionId} not found.[/]");
            return;
        }

        var (typeMarkup, icon) = GetTypeStyle(tx.Type);

        AnsiConsole.Write(new Rule($"[{GetTypeColor(tx.Type)} bold]Transaction #{tx.Id}[/]").RuleStyle(GetTypeColor(tx.Type)));

        // Info grid
        var grid = new Grid()
            .AddColumn()
            .AddColumn();
        grid.AddRow("[bold]Type:[/]", $"{icon} {typeMarkup}");
        grid.AddRow("[bold]Time:[/]", $"[grey]{tx.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}[/]");
        grid.AddRow("[bold]Status:[/]", $"[green]{tx.Status}[/]");
        grid.AddRow("[bold]Packages:[/]", $"[white]{tx.Entries.Count}[/]");

        AnsiConsole.Write(grid);
        AnsiConsole.WriteLine();

        if (tx.Entries.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey](No package changes recorded)[/]");
            return;
        }

        // Detail table
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]Action[/]").Width(10))
            .AddColumn(new TableColumn("[bold]Package[/]"))
            .AddColumn(new TableColumn("[bold]Old Version[/]"))
            .AddColumn(new TableColumn("[bold]New Version[/]"))
            .AddColumn(new TableColumn("[bold]Arch[/]").Width(10));

        foreach (var entry in tx.Entries)
        {
            var (actionMarkup, actionIcon) = GetActionStyle(entry.Action);

            table.AddRow(
                $"{actionIcon} {actionMarkup}",
                $"[white bold]{Markup.Escape(entry.PackageName)}[/]",
                entry.OldVersion != null ? $"[red]{Markup.Escape(entry.OldVersion)}[/]" : "[dim]—[/]",
                entry.NewVersion != null ? $"[green]{Markup.Escape(entry.NewVersion)}[/]" : "[dim]—[/]",
                $"[grey]{entry.Arch}[/]"
            );
        }

        AnsiConsole.Write(table);
    }

    // ──────────────────────────────────────────────────────────────
    // List Mode (Non-interactive)
    // ──────────────────────────────────────────────────────────────

    private async Task ShowListModeAsync(string dbPath)
    {
        var transactions = await AnsiConsole.Status()
            .StartAsync("Loading history...", _ => TransactionHistory.GetHistoryAsync(dbPath, 50, 0));

        if (transactions.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No transaction history found.[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]ID[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Timestamp[/]"))
            .AddColumn(new TableColumn("[bold]Type[/]"))
            .AddColumn(new TableColumn("[bold]Packages[/]"))
            .AddColumn(new TableColumn("[bold]Details[/]"));

        foreach (var tx in transactions)
        {
            var (typeMarkup, icon) = GetTypeStyle(tx.Type);
            var timeStr = tx.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            var pkgNames = tx.Entries.Select(e => e.PackageName).ToList();
            string pkgDisplay;
            if (pkgNames.Count == 0)
            {
                pkgDisplay = "[grey]—[/]";
            }
            else if (pkgNames.Count <= 3)
            {
                pkgDisplay = string.Join("[grey], [/]", pkgNames.Select(p => $"[white]{Markup.Escape(p)}[/]"));
            }
            else
            {
                var shown = pkgNames.Take(3).Select(p => $"[white]{Markup.Escape(p)}[/]");
                pkgDisplay = string.Join("[grey], [/]", shown) + $" [dim]+{pkgNames.Count - 3}[/]";
            }

            // Build a short summary of changes
            string details = tx.Type switch
            {
                "install" => $"[green]+{tx.Entries.Count}[/]",
                "remove"  => $"[red]-{tx.Entries.Count}[/]",
                "update"  => $"[blue]↑{tx.Entries.Count}[/]",
                _         => $"[grey]{tx.Entries.Count}[/]"
            };

            table.AddRow(
                $"[yellow]{tx.Id}[/]",
                $"[grey]{timeStr}[/]",
                $"{icon} {typeMarkup}",
                pkgDisplay,
                details
            );
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[dim]Showing latest {transactions.Count} transactions. Use --view=<id> for details.[/]");
    }

    // ──────────────────────────────────────────────────────────────
    // Rollback
    // ──────────────────────────────────────────────────────────────

    private async Task PerformRollbackAsync(CliConfiguration config, long targetTransactionId)
    {
        AuLogger.Info($"History: rollback requested to transaction #{targetTransactionId}");
        var dbPath = config.DbPath;
        var tx = await AnsiConsole.Status()
            .StartAsync("Loading transaction...", _ => TransactionHistory.GetTransactionAsync(dbPath, targetTransactionId));

        if (tx == null)
        {
            AnsiConsole.MarkupLine($"[red]Transaction #{targetTransactionId} not found.[/]");
            return;
        }

        if (tx.Entries.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]This transaction has no package entries. Nothing to rollback.[/]");
            return;
        }

        // Build rollback plan
        var rollbackActions = new List<RollbackAction>();
        var warnings = new List<string>();

        foreach (var entry in tx.Entries)
        {
            switch (entry.Action)
            {
                case "install":
                    // Rollback = remove the installed packages
                    rollbackActions.Add(new RollbackAction
                    {
                        Type = "remove",
                        PackageName = entry.PackageName,
                        Version = entry.NewVersion,
                        Arch = entry.Arch
                    });
                    break;

                case "remove":
                    // Rollback = reinstall the removed packages
                    rollbackActions.Add(new RollbackAction
                    {
                        Type = "install",
                        PackageName = entry.PackageName,
                        Version = entry.OldVersion,
                        Arch = entry.Arch
                    });
                    break;

                case "upgrade":
                    // Rollback = downgrade to old version
                    rollbackActions.Add(new RollbackAction
                    {
                        Type = "downgrade",
                        PackageName = entry.PackageName,
                        Version = entry.OldVersion,
                        Arch = entry.Arch
                    });
                    break;
            }
        }

        // Display rollback plan
        AnsiConsole.Write(new Rule("[red bold]⚠ Rollback Plan[/]").RuleStyle("red"));
        AnsiConsole.MarkupLine($"[yellow]Rolling back transaction #{tx.Id} ({tx.Type}) from {tx.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}[/]");
        AnsiConsole.WriteLine();

        var planTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Red)
            .AddColumn(new TableColumn("[bold]Action[/]"))
            .AddColumn(new TableColumn("[bold]Package[/]"))
            .AddColumn(new TableColumn("[bold]Target Version[/]"));

        foreach (var action in rollbackActions)
        {
            var (actionMarkup, actionIcon) = action.Type switch
            {
                "remove"    => ("[red]Remove[/]", "🗑"),
                "install"   => ("[green]Install[/]", "📦"),
                "downgrade" => ("[blue]Downgrade[/]", "⬇"),
                _           => ("[grey]?[/]", "?")
            };

            planTable.AddRow(
                $"{actionIcon} {actionMarkup}",
                $"[white bold]{Markup.Escape(action.PackageName)}[/]",
                action.Version != null ? $"[yellow]{Markup.Escape(action.Version)}[/]" : "[dim](latest)[/]"
            );
        }

        AnsiConsole.Write(planTable);

        if (warnings.Count > 0)
        {
            AnsiConsole.WriteLine();
            foreach (var w in warnings)
                AnsiConsole.MarkupLine($"[yellow]⚠ {w}[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[red bold]Warning:[/] Rollback affects system packages. Ensure you understand the consequences.");
        AnsiConsole.MarkupLine("[red bold]Warning:[/] Some package versions may no longer be available in repositories.");

        if (!AnsiConsole.Confirm("[bold]Proceed with rollback?[/]"))
        {
            AuLogger.Info("History: rollback cancelled by user.");
            AnsiConsole.MarkupLine("[dim]Rollback cancelled.[/]");
            return;
        }

        // Execute rollback
        await ExecuteRollbackAsync(config, rollbackActions);
    }

    private async Task ExecuteRollbackAsync(CliConfiguration config, List<RollbackAction> actions)
    {
        var toRemove = actions.Where(a => a.Type == "remove").ToList();
        var toInstallOrDowngrade = actions.Where(a => a.Type is "install" or "downgrade").ToList();

        var rpmLogs = new List<string>();

        // Phase 1: Remove packages (rollback of installs)
        if (toRemove.Count > 0)
        {
            AnsiConsole.Write(new Rule("[red]Removing Packages[/]").RuleStyle("grey"));

            var targets = string.Join(" ", toRemove.Select(a => a.PackageName));
            var psi = new ProcessStartInfo
            {
                FileName = "rpm",
                Arguments = $"--root {config.SysRoot} -evh {targets}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var removeSuccess = false;
            AnsiConsole.Status().Start("[red]Removing packages...[/]", ctx =>
            {
                using var process = Process.Start(psi);
                if (process == null) return;

                process.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data)) rpmLogs.Add(e.Data);
                };
                process.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data)) rpmLogs.Add($"[warn] {e.Data}");
                };

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                removeSuccess = process.ExitCode == 0;
            });

            if (removeSuccess)
                AnsiConsole.MarkupLine($"[green bold]✔ Removed {toRemove.Count} package(s).[/]");
            else
                AnsiConsole.MarkupLine("[red bold]Some removals failed. Check RPM output above.[/]");
        }

        // Phase 2: Reinstall/downgrade packages (rollback of removes and upgrades)
        if (toInstallOrDowngrade.Count > 0)
        {
            AnsiConsole.Write(new Rule("[blue]Reinstalling / Downgrading Packages[/]").RuleStyle("grey"));

            // Load repos to find package URLs
            var repoFiles = Directory.GetFiles(config.RepoDir, "*.sqlite");
            if (repoFiles.Length == 0)
            {
                AnsiConsole.MarkupLine("[red]No repository databases found. Run 'au sync' first.[/]");
                AnsiConsole.MarkupLine("[yellow]Cannot complete rollback — package downloads require repository data.[/]");
                return;
            }

            // Load available packages from repos
            var availablePackages = new List<Package>();
            await AnsiConsole.Status().StartAsync("Loading repositories...", async ctx =>
            {
                foreach (var dbFile in repoFiles)
                {
                    try
                    {
                        using var db = new RpmRepoDb(dbFile);
                        string repoId = Path.GetFileNameWithoutExtension(dbFile);
                        availablePackages.AddRange(db.GetAllPackages(repoId));
                    }
                    catch { /* skip broken repos */ }
                }
            });

            // Match rollback actions to repo packages
            var packagesToDownload = new List<Package>();
            var notFound = new List<RollbackAction>();

            foreach (var action in toInstallOrDowngrade)
            {
                // Find the best matching package in repos
                var candidates = availablePackages
                    .Where(p => p.Name.Equals(action.PackageName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                Package? selected = null;

                if (action.Version != null)
                {
                    // Try to find exact version
                    selected = candidates.FirstOrDefault(p =>
                        p.FullVersion.Equals(action.Version, StringComparison.OrdinalIgnoreCase));

                    // For downgrades, also try matching version without epoch
                    if (selected == null)
                    {
                        selected = candidates.FirstOrDefault(p =>
                            action.Version.Contains(p.Version) && action.Version.Contains(p.Release));
                    }
                }

                // Fallback to latest version if exact not found
                if (selected == null)
                {
                    selected = candidates
                        .OrderByDescending(p => p.FullVersion)
                        .FirstOrDefault();
                }

                if (selected != null)
                    packagesToDownload.Add(selected);
                else
                    notFound.Add(action);
            }

            if (notFound.Count > 0)
            {
                AnsiConsole.MarkupLine($"[yellow]⚠ {notFound.Count} package(s) not found in any repository:[/]");
                foreach (var nf in notFound)
                    AnsiConsole.MarkupLine($"  [yellow]• {Markup.Escape(nf.PackageName)} {nf.Version ?? ""}[/]");
            }

            if (packagesToDownload.Count == 0)
            {
                AnsiConsole.MarkupLine("[red]No packages available for download. Rollback is partial.[/]");
                return;
            }

            // Download
            var repoMgr = new RepoManager(config.SysRoot) { SkipSignatureCheck = config.SkipGpg };
            var packagePaths = new string[packagesToDownload.Count];
            var semaphore = new SemaphoreSlim(5);

            if (!Directory.Exists(config.CacheDir)) Directory.CreateDirectory(config.CacheDir);

            await AnsiConsole.Progress()
                .Columns(new ProgressColumn[]
                {
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new DownloadedColumn(),
                    new SpinnerColumn()
                })
                .StartAsync(async ctx =>
                {
                    var tasks = packagesToDownload.Select(async (pkg, index) =>
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

                            if (path != null)
                                packagePaths[index] = path;
                            else
                                AnsiConsole.MarkupLine($"[red]Download failed for {pkg.Name}[/]");
                        }
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLine($"[red]Download failed for {pkg.Name}:[/] {ex.Message}");
                        }
                        finally
                        {
                            task.StopTask();
                            semaphore.Release();
                        }
                    });
                    await Task.WhenAll(tasks);
                });

            // Install downloaded packages
            var validPaths = packagePaths.Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (validPaths.Count > 0)
            {
                try
                {
                    // Determine if we need --oldpackage flag for downgrades
                    bool hasDowngrades = toInstallOrDowngrade.Any(a => a.Type == "downgrade");

                    AnsiConsole.Status().Start("[cyan]Installing packages...[/]", ctx =>
                    {
                        if (hasDowngrades)
                        {
                            // Use RPM's --oldpackage to allow downgrades
                            var args = new List<string> { "-Uvh", "--oldpackage" };
                            if (config.SysRoot != "/")
                            {
                                args.Add("--root");
                                args.Add(config.SysRoot);
                            }
                            if (config.Force) args.Add("--force");
                            args.AddRange(validPaths.Select(p => $"\"{p}\""));

                            var psi = new ProcessStartInfo
                            {
                                FileName = "rpm",
                                Arguments = string.Join(" ", args),
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };
                            psi.Environment.Remove("LD_LIBRARY_PATH");
                            psi.Environment.Remove("LD_PRELOAD");

                            using var proc = Process.Start(psi);
                            if (proc != null)
                            {
                                proc.OutputDataReceived += (s, e) =>
                                {
                                    if (!string.IsNullOrWhiteSpace(e.Data)) rpmLogs.Add(e.Data);
                                };
                                proc.ErrorDataReceived += (s, e) =>
                                {
                                    if (!string.IsNullOrWhiteSpace(e.Data)) rpmLogs.Add($"[warn] {e.Data}");
                                };
                                proc.BeginOutputReadLine();
                                proc.BeginErrorReadLine();
                                proc.WaitForExit();

                                if (proc.ExitCode != 0)
                                    throw new Exception($"RPM transaction failed with exit code {proc.ExitCode}");
                            }
                        }
                        else
                        {
                            SystemUpdater.ApplyUpdates(validPaths, config.SysRoot, config.Force,
                                msg => rpmLogs.Add(msg));
                        }
                    });

                    AnsiConsole.MarkupLine($"[green bold]✔ Reinstalled {validPaths.Count} package(s).[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red bold]Installation failed:[/] {Markup.Escape(ex.Message)}");
                    if (rpmLogs.Count > 0)
                    {
                        AnsiConsole.Write(new Rule("[yellow]RPM Output[/]").RuleStyle("yellow"));
                        foreach (var log in rpmLogs)
                            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(log)}[/]");
                    }
                }
            }
        }

        // Record the rollback itself in history
        var rollbackEntries = actions.Select(a => new HistoryEntry
        {
            Action = a.Type == "remove" ? "remove" : a.Type == "install" ? "install" : "downgrade",
            PackageName = a.PackageName,
            NewVersion = a.Version,
            Arch = a.Arch ?? string.Empty
        });

        try
        {
            await TransactionHistory.RecordTransactionAsync(config.DbPath, "rollback", rollbackEntries);
            AnsiConsole.MarkupLine("[green bold]✔ Rollback transaction recorded in history.[/]");
        }
        catch
        {
            // Non-critical — rollback itself succeeded
        }

        AnsiConsole.WriteLine();
        AuLogger.Info($"History: rollback complete ({actions.Count} actions).");
        AnsiConsole.MarkupLine("[bold green]Rollback complete.[/]");
    }

    // ──────────────────────────────────────────────────────────────
    // Styling Helpers
    // ──────────────────────────────────────────────────────────────

    private static (string Markup, string Icon) GetTypeStyle(string type) => type switch
    {
        "install"  => ("[green bold]Install[/]",  "📦"),
        "remove"   => ("[red bold]Remove[/]",     "🗑"),
        "update"   => ("[blue bold]Update[/]",    "⬆"),
        "rollback" => ("[magenta bold]Rollback[/]", "⏪"),
        _          => ($"[grey]{type}[/]",         "?")
    };

    private static string GetTypeColor(string type) => type switch
    {
        "install"  => "green",
        "remove"   => "red",
        "update"   => "blue",
        "rollback" => "magenta",
        _          => "grey"
    };

    private static (string Markup, string Icon) GetActionStyle(string action) => action switch
    {
        "install"  => ("[green]Install[/]",  "📦"),
        "remove"   => ("[red]Remove[/]",     "🗑"),
        "upgrade"  => ("[blue]Upgrade[/]",   "⬆"),
        "downgrade"=> ("[yellow]Downgrade[/]","⬇"),
        _          => ($"[grey]{action}[/]",  "?")
    };

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