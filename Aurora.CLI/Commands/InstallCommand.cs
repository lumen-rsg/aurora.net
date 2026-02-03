using Aurora.Core.IO;
using Aurora.Core.Logic;
using Aurora.Core.Logic.Hooks; // Ensure Hook Logic is referenced
using Aurora.Core.Models;
using Aurora.Core.Net;
using Aurora.Core.Parsing;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class InstallCommand : ICommand
{
    public string Name => "install";
    public string Description => "Install a package (from repo or local file)";

    public async Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("Usage: install <package_name_or_file>");
        string inputArg = args[0];

        // --- 1. Initialization & Database Lock ---
        // We open the transaction early to prevent other processes from modifying the DB
        // while we calculate dependencies.
        using var tx = new Transaction(config.DbPath);
        
        try
        {
            var installedPkgs = tx.GetAllPackages();
            var repoMgr = new RepoManager(config.SysRoot) { SkipSignatureCheck = config.SkipGpg };
            
            Package? targetPkg = null;
            string? localFilePath = null;
            bool isLocalFile = File.Exists(inputArg) && (inputArg.EndsWith(".au") || inputArg.EndsWith(".tar.gz"));

            // --- 2. Identify Target ---
            await AnsiConsole.Status().StartAsync("Analyzing target...", async ctx =>
            {
                if (isLocalFile)
                {
                    localFilePath = Path.GetFullPath(inputArg);
                    targetPkg = PackageExtractor.ReadManifest(localFilePath);
                    // Ensure we get the file list for Hook matching later
                    targetPkg.Files = PackageExtractor.GetFileList(localFilePath); 
                    ctx.Status($"[blue]Identified local package:[/] {targetPkg.Name} v{targetPkg.Version}");
                }
                else
                {
                    ctx.Status($"[blue]Targeting repository package:[/] {inputArg}");
                }
                await Task.Delay(200); // UI feel
            });

            var pkgName = targetPkg?.Name ?? inputArg;

            // Check if already installed
            if (!config.Force && tx.Database.IsInstalled(pkgName))
            {
                AnsiConsole.MarkupLine($"[yellow]Warning:[/] Package [bold]{pkgName}[/] is already installed.");
                AnsiConsole.MarkupLine("[grey]Use --force to reinstall.[/]");
                return;
            }

            // --- 3. Load Repositories ---
            var availablePackages = new List<Package>();
            
            // Only load repos if we aren't just installing a standalone local file with no deps
            // (Though usually we need repos for deps anyway)
            if (Directory.Exists(config.RepoDir))
            {
                AnsiConsole.Status().Start("Loading repository metadata...", _ => 
                {
                    foreach (var file in Directory.GetFiles(config.RepoDir, "*.aurepo"))
                    {
                        try 
                        {
                            var repoContent = File.ReadAllText(file);
                            var parsedRepo = RepoParser.Parse(repoContent);
                            availablePackages.AddRange(parsedRepo.Packages);
                        } 
                        catch { /* Ignore malformed repos */ }
                    }
                });
            }

            // Inject local package into the available pool for the solver
            if (isLocalFile && targetPkg != null)
            {
                availablePackages.RemoveAll(p => p.Name == targetPkg.Name);
                availablePackages.Add(targetPkg);
            }
            else if (availablePackages.Count == 0)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] No repository data found. Run 'au sync' first.");
                return;
            }

            // --- 4. Resolve Dependencies ---
            List<Package> plan = [];
            try
            {
                AnsiConsole.Status().Start("Resolving dependencies...", _ => 
                {
                    var solver = new DependencySolver(availablePackages, installedPkgs);
                    plan = solver.Resolve(pkgName);
                    
                    // Validate Conflicts
                    ConflictValidator.Validate(plan, installedPkgs);
                });
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red bold]Dependency Resolution Failed:[/] {Markup.Escape(ex.Message)}");
                return;
            }

            // --- 5. User Confirmation ---
            PrintTransactionSummary(plan);

            if (!config.AssumeYes && !AnsiConsole.Confirm("Proceed with installation?"))
            {
                AnsiConsole.MarkupLine("[yellow]Operation cancelled.[/]");
                return;
            }

            // --- 6. Hook Engine Init ---
            var hookEngine = new HookEngine(config.SysRoot);
            
            // --- 7. Pre-Transaction Hooks ---
            // Files in 'plan' are what we ARE going to install.
            // Note: If this is an upgrade, we should technically pass 'Upgrade', but for MVP 'Install' covers file placement.
            AnsiConsole.Write(new Rule("[grey]Pre-Transaction Hooks[/]").RuleStyle("grey"));
            await hookEngine.RunHooksAsync(HookWhen.PreTransaction, plan, TriggerOperation.Install);

            // --- 8. Download Phase ---
            var packageFiles = new Dictionary<string, string>();
            if (isLocalFile && targetPkg != null && localFilePath != null)
            {
                packageFiles[targetPkg.Name] = localFilePath;
            }

            AnsiConsole.Write(new Rule("[cyan]Download Phase[/]").RuleStyle("cyan"));
            await AnsiConsole.Progress()
                .Columns(new ProgressColumn[]
                {
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new DownloadedColumn(),
                    new SpinnerColumn(),
                })
                .StartAsync(async ctx =>
                {
                    foreach (var pkg in plan)
                    {
                        if (packageFiles.ContainsKey(pkg.Name)) continue;

                        var task = ctx.AddTask($"[grey]Downloading {pkg.Name}...[/]");
                        
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

                            if (path == null) throw new FileNotFoundException($"Package {pkg.Name} not found in mirrors.");
                            packageFiles[pkg.Name] = path;
                        }
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLine($"[red]Download failed for {pkg.Name}:[/] {ex.Message}");
                            throw; // Abort transaction
                        }
                        finally 
                        {
                            task.StopTask();
                        }
                    }
                });

            // --- 9. Installation Phase ---
            AnsiConsole.Write(new Rule("[green]Installation Phase[/]").RuleStyle("green"));
            
            await AnsiConsole.Status().StartAsync("Installing packages...", async ctx =>
            {
                int current = 0;
                foreach (var pkg in plan)
                {
                    current++;
                    ctx.Status($"Installing [bold]{pkg.Name}[/] ({current}/{plan.Count})...");
                    
                    var pkgFile = packageFiles[pkg.Name];
                    var manifestFiles = new List<string>();

                    PackageInstaller.InstallPackage(pkgFile, config.SysRoot, (physical, manifest) =>
                    {
                        // Log to journal for rollback safety
                        tx.AppendToJournal(physical);
                        manifestFiles.Add(manifest);
                    });

                    // Update internal DB model with the list of files actually installed
                    pkg.Files = manifestFiles;
                    
                    // Update Database state
                    if (tx.Database.IsInstalled(pkg.Name))
                    {
                        // Handle re-install/upgrade logic if needed (remove old entry first?)
                        tx.RemovePackage(pkg.Name);
                    }
                    tx.RegisterPackage(pkg);
                }
            });

            // --- 10. Commit ---
            tx.Commit();

            // --- 11. Post-Transaction Hooks ---
            AnsiConsole.Write(new Rule("[grey]Post-Transaction Hooks[/]").RuleStyle("grey"));
            await hookEngine.RunHooksAsync(HookWhen.PostTransaction, plan, TriggerOperation.Install);

            AnsiConsole.MarkupLine($"\n[green bold]✔ Success![/] Installed {pkgName} and dependencies.");
        }
        catch (Exception)
        {
            // Transaction auto-rolls back on Dispose if not committed
            throw;
        }
    }

    private void PrintTransactionSummary(List<Package> plan)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Package");
        table.AddColumn("Version");
        table.AddColumn("Size");

        long totalSize = 0;
        foreach (var p in plan)
        {
            table.AddRow(
                new Markup($"[cyan]{p.Name}[/]"), 
                new Markup($"[grey]{p.Version}[/]"),
                new Markup(FormatBytes(p.InstalledSize))
            );
            totalSize += p.InstalledSize;
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"Total Install Size: [bold green]{FormatBytes(totalSize)}[/]");
        AnsiConsole.WriteLine();
    }

    private string FormatBytes(long bytes)
    {
        string[] suffix = { "B", "KB", "MB", "GB", "TB" };
        int i;
        double dblSByte = bytes;
        for (i = 0; i < suffix.Length && bytes >= 1024; i++, bytes /= 1024)
        {
            dblSByte = bytes / 1024.0;
        }
        return string.Format("{0:0.00} {1}", dblSByte, suffix[i]);
    }
}