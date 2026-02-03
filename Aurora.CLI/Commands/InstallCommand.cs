using System.Text.Json;
using Aurora.Core.IO;
using Aurora.Core.Logic;
using Aurora.Core.Logic.Hooks;
using Aurora.Core.Models;
using Aurora.Core.Net;
using Aurora.Core.Parsing;
using Aurora.Core.State; // Required for AuroraJsonContext
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

        using var tx = new Transaction(config.DbPath);
        
        try
        {
            var installedPkgs = tx.GetAllPackages();
            var repoMgr = new RepoManager(config.SysRoot) { SkipSignatureCheck = config.SkipGpg };
            
            Package? targetPkg = null;
            string? localFilePath = null;
            bool isLocalFile = File.Exists(inputArg) && (inputArg.EndsWith(".au") || inputArg.EndsWith(".tar.gz"));

            // 1. Identify Target
            if (isLocalFile)
            {
                localFilePath = Path.GetFullPath(inputArg);
                targetPkg = PackageExtractor.ReadManifest(localFilePath);
                targetPkg.Files = PackageExtractor.GetFileList(localFilePath); 
                AnsiConsole.MarkupLine($"[blue]Local Package:[/] {targetPkg.Name} v{targetPkg.Version}");
            }

            var pkgName = targetPkg?.Name ?? inputArg;

            if (!config.Force && tx.Database.IsInstalled(pkgName))
            {
                AnsiConsole.MarkupLine($"[yellow]Package [bold]{pkgName}[/] is already installed.[/]");
                return;
            }

            // --- 2. Load Repositories (UPDATED TO JSON) ---
            var availablePackages = new List<Package>();
            
            if (Directory.Exists(config.RepoDir))
            {
                var repoFiles = Directory.GetFiles(config.RepoDir, "*.json");
                
                foreach (var file in repoFiles)
                {
                    try 
                    {
                        var jsonContent = await File.ReadAllTextAsync(file);
                        var repoData = JsonSerializer.Deserialize(jsonContent, AuroraJsonContext.Default.Repository);
                        
                        if (repoData != null)
                        {
                            foreach (var rPkg in repoData.Packages)
                            {
                                availablePackages.Add(MapToInternalPackage(rPkg));
                            }
                        }
                    } 
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[yellow]Warning: Failed to load repo {Path.GetFileName(file)}: {ex.Message}[/]");
                    }
                }
            }

            if (isLocalFile && targetPkg != null)
            {
                availablePackages.RemoveAll(p => p.Name == targetPkg.Name);
                availablePackages.Add(targetPkg);
            }
            else if (availablePackages.Count == 0)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] No repository databases found. Run 'au sync' first.");
                return;
            }

            // --- 3. Resolve Dependencies ---
            List<Package> plan;
            try
            {
                var solver = new DependencySolver(availablePackages, installedPkgs);
                plan = solver.Resolve(pkgName);
                ConflictValidator.Validate(plan, installedPkgs);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red bold]Dependency Error:[/] {Markup.Escape(ex.Message)}");
                return;
            }

            // --- 4. Confirm & Run Hooks ---
            PrintTransactionSummary(plan);
            if (!config.AssumeYes && !AnsiConsole.Confirm("Proceed with installation?")) return;

            var hookEngine = new HookEngine(config.SysRoot);
            await hookEngine.RunHooksAsync(HookWhen.PreTransaction, plan, TriggerOperation.Install);
            
            // --- 5. Download Phase (PARALLEL) ---
            var packageFiles = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
            if (isLocalFile && targetPkg != null && localFilePath != null)
                packageFiles[targetPkg.Name] = localFilePath;
            
            var semaphore = new SemaphoreSlim(12); 

            AnsiConsole.Write(new Rule("[cyan]Downloading Assets[/]").RuleStyle("grey"));
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
                    var tasks = plan.Where(p => !packageFiles.ContainsKey(p.Name)).Select(async pkg =>
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

            // --- 6. Install Phase ---
            await AnsiConsole.Status().StartAsync("Installing...", async ctx =>
            {
                foreach (var pkg in plan)
                {
                    ctx.Status($"Installing [bold]{pkg.Name}[/]...");
                    var pkgFile = packageFiles[pkg.Name];
                    var manifestFiles = new List<string>();

                    PackageInstaller.InstallPackage(pkgFile, config.SysRoot, (physical, manifest) =>
                    {
                        tx.AppendToJournal(physical);
                        manifestFiles.Add(manifest);
                    });

                    pkg.Files = manifestFiles;
                    if (tx.Database.IsInstalled(pkg.Name)) tx.RemovePackage(pkg.Name);
                    tx.RegisterPackage(pkg);
                }
            });

            tx.Commit();
            await hookEngine.RunHooksAsync(HookWhen.PostTransaction, plan, TriggerOperation.Install);
            AnsiConsole.MarkupLine($"\n[green bold]✔ Installed {pkgName} successfully.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red bold]FATAL ERROR:[/] {Markup.Escape(ex.Message)}");
            throw; 
        }
    }

    // Helper to map JSON Repo Package to logic model
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
            InstallReason = "explicit",
            FileName = rPkg.FileName
        };
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
            table.AddRow($"[cyan]{p.Name}[/]", $"[grey]{p.Version}[/]", FormatBytes(p.InstalledSize));
            totalSize += p.InstalledSize;
        }
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"Total Size: [bold green]{FormatBytes(totalSize)}[/]\n");
    }

    private string FormatBytes(long bytes)
    {
        string[] suffix = { "B", "KB", "MB", "GB", "TB" };
        int i; double dblSByte = bytes;
        for (i = 0; i < suffix.Length && bytes >= 1024; i++, bytes /= 1024) dblSByte = bytes / 1024.0;
        return string.Format("{0:0.00} {1}", dblSByte, suffix[i]);
    }
}