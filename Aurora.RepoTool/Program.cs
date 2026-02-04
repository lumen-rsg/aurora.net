using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Aurora.Core.IO;
using Aurora.Core.Models;
using Aurora.Core.Security;
using Spectre.Console;

namespace Aurora.RepoTool;

class Program
{
    static async Task Main(string[] args)
    {
        AnsiConsole.Write(new FigletText("AURORA REPO").Color(Color.Cyan1));

        if (args.Length < 2 || args[0] != "generate")
        {
            PrintHelp();
            return;
        }

        string repoDir = Path.GetFullPath(args[1]);
        string repoName = args.Length > 2 ? args[2] : Path.GetFileName(repoDir.TrimEnd(Path.DirectorySeparatorChar));
        
        string jsonFile = Path.Combine(repoDir, $"{repoName}.json");
        
        if (!Directory.Exists(repoDir))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Directory not found: {repoDir}");
            return;
        }

        var repository = new Repository
        {
            Name = repoName,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        // 1. Scan Files
        var files = Directory.GetFiles(repoDir, "*.au");
        if (files.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No packages found.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[blue]Scanning {files.Length} packages using {Environment.ProcessorCount} threads...[/]");

        // Thread-safe collection
        var processedPackages = new ConcurrentBag<RepoPackage>();
        var errors = new ConcurrentBag<string>();
        
        // Timer for performance metrics
        var stopwatch = Stopwatch.StartNew();

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(new ProgressColumn[] 
            {
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn(),
                new ElapsedTimeColumn()
            })
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask($"[green]Indexing 0/{files.Length}[/]");
                task.MaxValue = files.Length;

                // MaxDegreeOfParallelism = -1 (Default) lets .NET decide based on CPU cores
                // You can cap this (e.g. = 8) if disk IO is the bottleneck
                var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };

                int count = 0;

                await Parallel.ForEachAsync(files, parallelOptions, async (file, token) =>
                {
                    try
                    {
                        // A. Extract Metadata (.PKGINFO)
                        // This involves reading the archive stream. For huge files (aotriton), this is the bottleneck.
                        var internalPkg = PackageExtractor.ReadManifest(file);
                        var fileInfo = new FileInfo(file);

                        // B. Compute SHA256
                        // Also IO heavy for large files
                        var checksum = HashHelper.ComputeFileHash(file);

                        // Map to Repo Model
                        var repoPkg = new RepoPackage
                        {
                            Name = internalPkg.Name,
                            Version = internalPkg.Version,
                            Arch = internalPkg.Arch,
                            Description = internalPkg.Description ?? "",
                            FileName = fileInfo.Name,
                            CompressedSize = fileInfo.Length,
                            InstalledSize = internalPkg.InstalledSize,
                            Checksum = checksum,
                            Url = internalPkg.Url ?? "",
                            Packager = internalPkg.Maintainer ?? "Aurora",
                            BuildDate = internalPkg.BuildDate,
                            
                            License = internalPkg.Licenses,
                            Depends = internalPkg.Depends,
                            Provides = internalPkg.Provides,
                            Conflicts = internalPkg.Conflicts,
                            Replaces = internalPkg.Replaces
                        };

                        processedPackages.Add(repoPkg);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"[red]{Path.GetFileName(file)}: {ex.Message}[/]");
                    }
                    finally
                    {
                        // Thread-safe increment
                        int current = Interlocked.Increment(ref count);
                        task.Value = current;
                        task.Description = $"[green]Indexing {current}/{files.Length}[/]";
                    }
                });
            });

        stopwatch.Stop();

        // Print Errors if any
        if (!errors.IsEmpty)
        {
            AnsiConsole.Write(new Rule("[red]Errors[/]"));
            foreach (var err in errors) AnsiConsole.MarkupLine(err);
        }

        // 2. Sort and Assign
        // ConcurrentBag is unordered, but repo lists should be deterministic (sorted by name)
        repository.Packages = processedPackages.OrderBy(p => p.Name).ToList();
        repository.Count = repository.Packages.Count;

        AnsiConsole.MarkupLine($"[blue]Processed {repository.Count} packages in {stopwatch.Elapsed.TotalSeconds:F1}s[/]");

        // 3. Write JSON
        AnsiConsole.MarkupLine($"[blue]Writing database to {jsonFile}...[/]");
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(repository, RepoContext.Default.Repository);
        await File.WriteAllBytesAsync(jsonFile, jsonBytes);

        // 4. Sign
        AnsiConsole.MarkupLine("[blue]Signing database...[/]");
        try
        {
            GpgHelper.SignFile(jsonFile);
            AnsiConsole.MarkupLine($"[green]✔ Database created and signed.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning: GPG signing failed: {ex.Message}[/]");
        }
    }

    static void PrintHelp()
    {
        var table = new Table().Border(TableBorder.Rounded).AddColumn("Usage");
        table.AddRow("au-repotool generate <dir> [repo_name]");
        AnsiConsole.Write(table);
    }
}