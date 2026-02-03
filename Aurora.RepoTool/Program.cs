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
        
        // Define Output Files
        string jsonFile = Path.Combine(repoDir, $"{repoName}.json"); // e.g., core.json
        string sigFile = $"{jsonFile}.sig";

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

        AnsiConsole.MarkupLine($"[blue]Scanning {files.Length} packages...[/]");

        await AnsiConsole.Progress().StartAsync(async ctx =>
        {
            var task = ctx.AddTask("[green]Indexing...[/]");
            double step = 100.0 / files.Length;

            foreach (var file in files)
            {
                task.Description = $"[grey]{Path.GetFileName(file)}[/]";
                
                try
                {
                    // Extract Metadata using the Core library (reads .PKGINFO)
                    var internalPkg = PackageExtractor.ReadManifest(file);
                    var fileInfo = new FileInfo(file);

                    // Map to Repo Model
                    var repoPkg = new RepoPackage
                    {
                        Name = internalPkg.Name,
                        Version = internalPkg.Version,
                        Arch = internalPkg.Arch,
                        Description = internalPkg.Description ?? "",
                        FileName = fileInfo.Name, // Relative filename
                        CompressedSize = fileInfo.Length,
                        InstalledSize = internalPkg.InstalledSize,
                        Checksum = HashHelper.ComputeFileHash(file), // SHA256 of the archive
                        Url = internalPkg.Url ?? "",
                        Packager = internalPkg.Maintainer ?? "Aurora",
                        BuildDate = internalPkg.BuildDate,
                        
                        // Lists
                        License = internalPkg.Licenses,
                        Depends = internalPkg.Depends,
                        Provides = internalPkg.Provides,
                        Conflicts = internalPkg.Conflicts,
                        Replaces = internalPkg.Replaces
                    };

                    repository.Packages.Add(repoPkg);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Failed to parse {Path.GetFileName(file)}: {ex.Message}[/]");
                }

                task.Increment(step);
            }
        });

        repository.Count = repository.Packages.Count;

        // 2. Write JSON (AOT Safe)
        AnsiConsole.MarkupLine($"[blue]Writing database to {jsonFile}...[/]");
        
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(repository, RepoContext.Default.Repository);
        await File.WriteAllBytesAsync(jsonFile, jsonBytes);

        // 3. Sign
        AnsiConsole.MarkupLine("[blue]Signing database...[/]");
        try
        {
            GpgHelper.SignFile(jsonFile); // Generates .json.sig
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