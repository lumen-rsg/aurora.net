using System.Text;
using Aurora.Core.IO;
using Aurora.Core.Models;
using Aurora.Core.Security;
using Spectre.Console;

class Program
{
    static void Main(string[] args)
    {
        AnsiConsole.Write(new FigletText("AU-REPOTOOL").Color(Color.Purple));

        if (args.Length < 2)
        {
            PrintHelp();
            return;
        }

        string command = args[0].ToLower();
        string targetDir = Path.GetFullPath(args[1]);

        try
        {
            switch (command)
            {
                case "generate":

                    string name = args.Length > 2 ? args[2] : Path.GetFileName(targetDir.TrimEnd(Path.DirectorySeparatorChar));
                    GenerateRepo(targetDir, name);
                    break;
                case "init":
                    InitRepo(targetDir);
                    break;
                default:
                    AnsiConsole.MarkupLine($"[red]Unknown command:[/] {command}");
                    break;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]Fatal Error:[/] {ex.Message}");
        }
    }

    static void InitRepo(string path)
    {
        Directory.CreateDirectory(path);
        AnsiConsole.MarkupLine($"[green]Initialized empty repository directory at:[/] {path}");
        AnsiConsole.MarkupLine("[grey]Place your .au files here and run 'au-repotool generate <dir>'[/]");
    }

    static void GenerateRepo(string repoDir, string repoName)
    
    {
        if (!Directory.Exists(repoDir)) throw new DirectoryNotFoundException(repoDir);

        // Use the repoName for the file
        var repoFileName = $"{repoName}.aurepo";
        var repoFile = Path.Combine(repoDir, repoFileName);
        var packages = new List<Package>();

        // 1. Scan for .au files
        var files = Directory.GetFiles(repoDir, "*.au");
        AnsiConsole.MarkupLine($"[blue]Scanning {files.Length} packages...[/]");

        AnsiConsole.Progress()
            .Start(ctx =>
            {
                var task = ctx.AddTask("[green]Processing packages[/]");
                double step = 100.0 / files.Length;

                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file);
                    task.Description = $"[grey]Processing {fileName}[/]";

                    // Extract Manifest
                    var pkg = PackageExtractor.ReadManifest(file);
                    
                    // Extract File List (for deletion tracking)
                    pkg.Files = PackageExtractor.GetFileList(file);
                    
                    // Compute SHA256
                    pkg.Checksum = HashHelper.ComputeFileHash(file);
                    
                    // Get Physical Size
                    pkg.InstalledSize = new FileInfo(file).Length;

                    packages.Add(pkg);
                    task.Increment(step);
                }
            });

        // 2. Build the .aurepo YAML
        var sb = new StringBuilder();
        sb.AppendLine("metadata:");
        sb.AppendLine("  creator: \"Aurora RepoTool v1.0\"");
        sb.AppendLine($"  timestamp: {DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
        sb.AppendLine($"  description: \"Repository generated on {DateTime.Now}\"");
        sb.AppendLine();
        sb.AppendLine("packages:");

        foreach (var p in packages)
        {
            sb.AppendLine($"  - name: \"{p.Name}\"");
            sb.AppendLine($"    version: \"{p.Version}\"");
            sb.AppendLine($"    arch: \"{p.Arch}\"");
            sb.AppendLine($"    description: \"{p.Description}\"");
            sb.AppendLine($"    url: \"{p.Url}\"");
            sb.AppendLine($"    checksum: \"{p.Checksum}\"");
            sb.AppendLine($"    installed_size: {p.InstalledSize}");
            sb.AppendLine($"    build_date: {p.BuildDate}");

            if (p.Depends.Any())
            {
                sb.AppendLine("    depends:");
                foreach (var d in p.Depends) sb.AppendLine($"      - \"{d}\"");
            }

            if (p.Conflicts.Any())
            {
                sb.AppendLine("    conflicts:");
                foreach (var c in p.Conflicts) sb.AppendLine($"      - \"{c}\"");
            }

            if (p.Provides.Any())
            {
                sb.AppendLine("    provides:");
                foreach (var pr in p.Provides) sb.AppendLine($"      - \"{pr}\"");
            }

            sb.AppendLine("    files:");
            foreach (var f in p.Files) sb.AppendLine($"      - \"{f}\"");
            
            sb.AppendLine(); // Spacer between packages
        }

        File.WriteAllText(repoFile, sb.ToString());
        AnsiConsole.MarkupLine($"[green]Successfully generated:[/] {repoFile}");

        // 3. GPG Signing
        AnsiConsole.MarkupLine("[blue]Signing repository database...[/]");
        try
        {
            GpgHelper.SignFile(repoFile);
            AnsiConsole.MarkupLine($"[green]Generated signature:[/] {repoFile}.asc");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning: GPG Signing failed. {ex.Message}[/]");
        }
    }

    static void PrintHelp()
    {
        var table = new Table().AddColumn("Command").AddColumn("Description");
        table.AddRow("init <dir>", "Create a new repository directory");
        table.AddRow("generate <dir>", "Scan .au files and build .aurepo database");
        AnsiConsole.Write(table);
    }
}