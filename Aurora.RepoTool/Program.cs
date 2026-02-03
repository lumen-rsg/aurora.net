using System.Text;
using Aurora.Core.IO;
using Aurora.Core.Models;
using Aurora.Core.Security;
using Spectre.Console;

namespace Aurora.RepoTool;

class Program
{
    static void Main(string[] args)
    {
        AnsiConsole.Write(new FigletText("AU-REPOTOOL").Color(Color.Purple));

        if (args.Length < 1)
        {
            PrintHelp();
            return;
        }

        string command = args[0].ToLower();

        try
        {
            switch (command)
            {
                case "init":
                    if (args.Length < 2) throw new ArgumentException("Usage: init <dir>");
                    InitRepo(args[1]);
                    break;

                case "generate":
                    if (args.Length < 2) throw new ArgumentException("Usage: generate <dir> [repo_name]");
                    string targetDir = Path.GetFullPath(args[1]);
                    // Default repo name is the folder name
                    string repoName = args.Length > 2 ? args[2] : Path.GetFileName(targetDir.TrimEnd(Path.DirectorySeparatorChar));
                    GenerateRepo(targetDir, repoName);
                    break;

                default:
                    AnsiConsole.MarkupLine($"[red]Unknown command:[/] {command}");
                    PrintHelp();
                    break;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.Write(new Rule("[red]Fatal Error[/]").RuleStyle("red"));
            AnsiConsole.MarkupLine($"[bold red]Error:[/] {Markup.Escape(ex.Message)}");
        }
    }

    static void InitRepo(string path)
    {
        Directory.CreateDirectory(path);
        AnsiConsole.MarkupLine($"[green]✔ Initialized empty repository directory at:[/] [blue]{path}[/]");
        AnsiConsole.MarkupLine("[grey]Place your .au files here and run 'au-repotool generate <dir>'[/]");
    }

    static void GenerateRepo(string repoDir, string repoName)
    {
        if (!Directory.Exists(repoDir)) throw new DirectoryNotFoundException($"Directory not found: {repoDir}");

        var repoFileName = $"{repoName}.aurepo";
        var repoFile = Path.Combine(repoDir, repoFileName);
        var packages = new List<Package>();

        // 1. Scan for .au files
        var files = Directory.GetFiles(repoDir, "*.au");
        if (files.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No .au packages found in directory. Nothing to do.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[blue]Building Repository:[/] [bold]{repoName}[/]");
        AnsiConsole.MarkupLine($"[grey]Found {files.Length} packages.[/]");

        AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(new ProgressColumn[] 
            {
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn(),
            })
            .Start(ctx =>
            {
                var task = ctx.AddTask("[green]Processing packages[/]");
                double step = 100.0 / files.Length;

                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file);
                    task.Description = $"[grey]Indexing {fileName}[/]";

                    // A. Extract metadata from within the archive (aurora.meta)
                    var pkg = PackageExtractor.ReadManifest(file);
                    
                    // B. Extract full file list for database tracking
                    pkg.Files = PackageExtractor.GetFileList(file);
                    
                    // C. Integrity check
                    pkg.Checksum = HashHelper.ComputeFileHash(file);
                    
                    // D. Calculate logical and physical size
                    pkg.InstalledSize = new FileInfo(file).Length;

                    // E. Smart Provisions Logic
                    // We keep existing versioned provides (like libreadline.so=8-64) 
                    // and only add missing unversioned sonames from the binary files.
                    var existingProvides = new HashSet<string>(pkg.Provides, StringComparer.OrdinalIgnoreCase);
                    
                    foreach (var f in pkg.Files)
                    {
                        if (f.EndsWith(".so") || f.Contains(".so."))
                        {
                            var soname = Path.GetFileName(f);
                            
                            // Only add if not already covered by a versioned string
                            bool alreadyProvided = existingProvides.Any(p => 
                                p.Equals(soname, StringComparison.OrdinalIgnoreCase) || 
                                p.StartsWith(soname + "=", StringComparison.OrdinalIgnoreCase));

                            if (!alreadyProvided)
                            {
                                pkg.Provides.Add(soname);
                                existingProvides.Add(soname);
                            }
                        }
                    }

                    packages.Add(pkg);
                    task.Increment(step);
                }
                task.Description = "[green]Processing complete[/]";
            });

        // 2. Build the .aurepo YAML (Strict Contract)
        var sb = new StringBuilder();
        sb.AppendLine("metadata:");
        sb.AppendLine("  creator: \"Aurora RepoTool v1.0\"");
        sb.AppendLine($"  timestamp: {DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
        sb.AppendLine($"  description: \"Repository '{repoName}' generated on {DateTime.Now}\"");
        sb.AppendLine();
        sb.AppendLine("packages:");

        foreach (var p in packages)
        {
            sb.AppendLine($"  - name: \"{p.Name}\"");
            sb.AppendLine($"    version: \"{p.Version}\"");
            sb.AppendLine($"    arch: \"{p.Arch}\"");
            sb.AppendLine($"    description: \"{p.Description?.Replace("\"", "\\\"")}\"");
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

            if (p.Replaces.Any())
            {
                sb.AppendLine("    replaces:");
                foreach (var r in p.Replaces) sb.AppendLine($"      - \"{r}\"");
            }

            if (p.Files.Any())
            {
                sb.AppendLine("    files:");
                foreach (var f in p.Files) sb.AppendLine($"      - \"{f}\"");
            }
            
            sb.AppendLine(); // Spacer between packages
        }

        File.WriteAllText(repoFile, sb.ToString());
        AnsiConsole.MarkupLine($"[green]✔ Successfully generated:[/] [white]{repoFileName}[/]");

        // 3. GPG Signing
        AnsiConsole.MarkupLine("[blue]Signing repository database...[/]");
        try
        {
            GpgHelper.SignFile(repoFile);
            AnsiConsole.MarkupLine($"[green]✔ Generated signature:[/] [white]{repoFileName}.asc[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠ Warning:[/] GPG Signing failed. {ex.Message}");
            AnsiConsole.MarkupLine("[grey]Ensure you have a default GPG key configured in your environment.[/]");
        }

        AnsiConsole.Write(new Rule().RuleStyle("grey"));
        AnsiConsole.MarkupLine($"[bold green]Done![/] Repository [cyan]{repoName}[/] is ready for deployment.");
    }

    static void PrintHelp()
    {
        var table = new Table().Border(TableBorder.Rounded).AddColumn("Command").AddColumn("Description");
        table.AddRow("[yellow]init[/] <dir>", "Initialize a new directory for Aurora packages");
        table.AddRow("[yellow]generate[/] <dir> [name]", "Scan packages and generate a signed .aurepo database");
        AnsiConsole.Write(table);
    }
}