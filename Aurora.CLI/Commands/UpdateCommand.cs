using System.Text.Json;
using Aurora.Core.IO;
using Aurora.Core.Logic;
using Aurora.Core.Models;
using Aurora.Core.Net;
using Aurora.Core.State; // For AuroraJsonContext
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class UpdateCommand : ICommand
{
    public string Name => "update";
    public string Description => "Update system packages from repositories";

    public async Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        // --- 1. Validation ---
        if (!Directory.Exists(config.RepoDir))
        {
            AnsiConsole.MarkupLine($"[red]No repository directory found at {config.RepoDir}.[/]");
            AnsiConsole.MarkupLine("[yellow]Run 'au sync' first.[/]");
            return;
        }

        var repoFiles = Directory.GetFiles(config.RepoDir, "*.json");
        if (repoFiles.Length == 0)
        {
            AnsiConsole.MarkupLine($"[red]No repository databases found in {config.RepoDir}.[/]");
            AnsiConsole.MarkupLine("[yellow]Run 'au sync' first.[/]");
            return;
        }

        // --- 2. Transaction Start ---
        // We open the transaction early to read the installed state securely.
        using var tx = new Transaction(config.DbPath);
        
        try
        {
            // --- 3. Load Repository Data (AOT JSON) ---
            var availablePackages = new Dictionary<string, Package>();
            
            await AnsiConsole.Status().StartAsync("Reading repositories...", async ctx =>
            {
                foreach (var file in repoFiles)
                {
                    try 
                    {
                        var jsonContent = await File.ReadAllTextAsync(file);
                        // Deserialize using the Source Generated Context
                        var repoData = JsonSerializer.Deserialize(jsonContent, AuroraJsonContext.Default.Repository);
                        
                        if (repoData != null)
                        {
                            foreach (var rPkg in repoData.Packages)
                            {
                                // Map RepoPackage (JSON) -> Package (Internal)
                                // If duplicates exist across repos, last one wins (simple priority)
                                availablePackages[rPkg.Name] = MapToInternalPackage(rPkg);
                            }
                        }
                    } 
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[yellow]Warning: Failed to parse {Path.GetFileName(file)}: {ex.Message}[/]");
                    }
                }
            });

            // --- 4. Calculate Updates ---
            var installedPackages = tx.GetAllPackages();
            var updatePlan = new List<(Package NewPkg, string OldVersion)>();

            foreach (var local in installedPackages)
            {
                if (availablePackages.TryGetValue(local.Name, out var remote))
                {
                    if (VersionComparer.IsNewer(local.Version, remote.Version))
                    {
                        updatePlan.Add((remote, local.Version));
                    }
                }
            }

            if (updatePlan.Count == 0)
            {
                AnsiConsole.MarkupLine("[green]System is up to date.[/]");
                return;
            }

            // --- 5. Present Plan ---
            AnsiConsole.Write(new Rule("[yellow]System Update[/]").RuleStyle("grey"));
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Package");
            table.AddColumn("Old Version");
            table.AddColumn("New Version");
            table.AddColumn("Size");

            long totalDownloadSize = 0;
            foreach (var (pkg, oldVer) in updatePlan)
            {
                table.AddRow(
                    new Markup($"[cyan]{pkg.Name}[/]"),
                    new Markup($"[grey]{oldVer}[/]"),
                    new Markup($"[green]{pkg.Version}[/]"),
                    new Markup(FormatBytes(pkg.InstalledSize)) // Note: This is installed size, compressed is in RepoPackage
                );
                // We assume we download the full package size roughly matches installed/3 for display,
                // or if we mapped CompressedSize we could show that.
                totalDownloadSize += pkg.InstalledSize; 
            }
            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"Total packages: [bold]{updatePlan.Count}[/]");

            if (!config.AssumeYes && !AnsiConsole.Confirm("Proceed with update?"))
            {
                AnsiConsole.MarkupLine("[yellow]Operation cancelled.[/]");
                return;
            }

            // --- 6. Download Phase (PARALLEL) ---
            var repoMgr = new RepoManager(config.SysRoot) { SkipSignatureCheck = config.SkipGpg };
            var packageFiles = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
            var semaphore = new SemaphoreSlim(12);

            AnsiConsole.Write(new Rule("[cyan]Downloading Updates[/]").RuleStyle("grey"));
            if (!Directory.Exists(config.CacheDir)) Directory.CreateDirectory(config.CacheDir);
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
                    var tasks = updatePlan.Select(async pair =>
                    {
                        var pkg = pair.NewPkg;
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

                            if (path == null) throw new FileNotFoundException($"Update for {pkg.Name} not found.");
                            packageFiles[pkg.Name] = path;
                        }
                        finally 
                        {
                            task.StopTask();
                            semaphore.Release();
                        }
                    });

                    await Task.WhenAll(tasks);
                });

            // --- 7. Installation Phase ---
            // TODO: Run Hooks (Pre-Transaction) here if integrated
            
            AnsiConsole.Write(new Rule("[green]Applying Updates[/]").RuleStyle("grey"));
            
            await AnsiConsole.Status().StartAsync("Installing...", async ctx =>
            {
                foreach (var (pkg, _) in updatePlan)
                {
                    ctx.Status($"Updating [bold]{pkg.Name}[/]...");
                    var pkgFile = packageFiles[pkg.Name];
                    var manifestFiles = new List<string>();

                    // Atomic Install:
                    // 1. Extract files
                    PackageInstaller.InstallPackage(pkgFile, config.SysRoot, (physical, manifest) =>
                    {
                        tx.AppendToJournal(physical);
                        manifestFiles.Add(manifest);
                    });

                    // 2. Update DB Memory
                    pkg.Files = manifestFiles;
                    
                    // 3. Remove old record, Add new record
                    // Note: Physical removal of old files not owned by new package 
                    // is handled by complex logic (checking ownership), usually skipped in simple overwrites
                    // or handled by a dedicated cleanup pass.
                    // For MVP: We overwrite files. Old files that don't exist in new pkg become orphans.
                    tx.RemovePackage(pkg.Name);
                    tx.RegisterPackage(pkg);
                }
            });

            // --- 8. Commit ---
            tx.Commit();
            
            // TODO: Run Hooks (Post-Transaction) here if integrated

            AnsiConsole.MarkupLine("[green bold]✔ System updated successfully.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red bold]Update Failed:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.MarkupLine("[yellow]Transaction rolled back (database consistent, check file consistency).[/]");
        }
    }

    private Package MapToInternalPackage(RepoPackage rPkg)
    {
        return new Package
        {
            Name = rPkg.Name,
            Version = rPkg.Version,
            Arch = rPkg.Arch,
            Description = rPkg.Description,
            Maintainer = rPkg.Packager,
            Url = rPkg.Url,
            Licenses = rPkg.License,
            BuildDate = rPkg.BuildDate,
            Depends = rPkg.Depends,
            Conflicts = rPkg.Conflicts,
            Provides = rPkg.Provides,
            Replaces = rPkg.Replaces,
            Checksum = rPkg.Checksum,
            InstalledSize = rPkg.InstalledSize,
            FileName = rPkg.FileName,
            InstallReason = "explicit" // Updates maintain the existing reason usually, but new objects default to explicit
        };
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