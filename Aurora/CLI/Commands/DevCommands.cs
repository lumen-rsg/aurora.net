using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Aurora.Core.Models;
using Aurora.Core.Security;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class GenRepoCommand : ICommand
{
    public string Name => "gen-repo";
    public string Description => "Generate test repository";

    public Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("Usage: gen-repo <output_dir>");
        string outputDir = args[0];
        Directory.CreateDirectory(outputDir);
        var packages = new List<Package>();

        void MakePkg(string name, string ver, string[] deps, string[] conflicts)
        {
            var pkg = new Package 
            { 
                Name = name, Version = ver, Arch = "x86_64", 
                Description = $"Auto-generated {name}", 
                Depends = deps.ToList(),
                Conflicts = conflicts.ToList()
            };
            packages.Add(pkg);

            var filename = $"{name}-{ver}-x86_64.au";
            var path = Path.Combine(outputDir, filename);
            
            // FIX: Wrap creation in a scope so 'fs' is disposed/closed immediately
            {
                using var fs = File.Create(path);
                using var gz = new GZipStream(fs, CompressionLevel.Fastest);
                using var tar = new TarWriter(gz, TarEntryFormat.Pax);

                var content = Encoding.UTF8.GetBytes($"Binary for {name}");
                var ms = new MemoryStream(content);
                var entry = new PaxTarEntry(TarEntryType.RegularFile, $"usr/bin/{name}");
                entry.DataStream = ms;
                tar.WriteEntry(entry);
                // ms is disposed by TarWriter or GC, no file lock issues here
            } 
            // <--- File is now closed.

            // Now it is safe to read the file for hashing
            var hash = HashHelper.ComputeFileHash(path);
            pkg.Checksum = hash;

            AnsiConsole.MarkupLine($"Created [cyan]{filename}[/] ({hash.Substring(0, 8)}...)");
        }

        // --- DEFINE THE WORLD ---
        MakePkg("glibc", "2.38", new string[]{}, new string[]{});
        MakePkg("ncurses", "6.4", new []{"glibc"}, new string[]{});
        MakePkg("vim", "9.0", new []{"ncurses"}, new []{"nano"}); 
        MakePkg("nano", "7.2", new []{"ncurses"}, new []{"vim"});

        var sb = new StringBuilder();
        foreach (var p in packages)
        {
            // Use Strict YAML format
            sb.AppendLine("---");
            sb.AppendLine("package:");
            sb.AppendLine($"  name: {p.Name}");
            sb.AppendLine($"  version: {p.Version}");
            sb.AppendLine($"  architecture: {p.Arch}");
            sb.AppendLine($"  description: {p.Description}");
            
            sb.AppendLine("metadata:");
            if (p.Conflicts.Any())
            {
                sb.AppendLine("  conflicts:");
                foreach (var c in p.Conflicts) sb.AppendLine($"    - {c}");
            }

            sb.AppendLine("dependencies:");
            sb.AppendLine("  runtime:");
            if (p.Depends.Any())
            {
                foreach (var d in p.Depends) sb.AppendLine($"    - {d}");
            }
            
            sb.AppendLine("files:");
            if (!string.IsNullOrEmpty(p.Checksum))
            {
                sb.AppendLine($"  source_hash: {p.Checksum}");
            }
            sb.AppendLine();
        }

        File.WriteAllText(Path.Combine(outputDir, "repo.yaml"), sb.ToString());
        AnsiConsole.MarkupLine($"[green]Repository generated at {outputDir}[/]");
        return Task.CompletedTask;
    }
}

public class TestGenCommand : ICommand
{
    public string Name => "test-gen";
    public string Description => "Generate single test package";

    public Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        if (args.Length < 2) throw new ArgumentException("Usage: test-gen <filename> <content>");
        string path = args[0];
        string contentStr = args[1];

        // Ensure we start clean
        if (File.Exists(path)) File.Delete(path);

        using (var fs = File.Create(path))
        using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
        using (var tar = new TarWriter(gz, TarEntryFormat.Ustar)) // Switch to Ustar for max compat
        {
            var bytes = Encoding.UTF8.GetBytes($"Data for {contentStr}");
            
            var ms = new MemoryStream(bytes);
            
            // Explicitly set size
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "usr/bin/sql_test");
            entry.DataStream = ms;
            tar.WriteEntry(entry);
        } // Dispose flushes everything
        
        return Task.CompletedTask;
    }
    
    
}

public class TestParserCommand : ICommand
{
    public string Name => "test-parser";
    public string Description => "Test new YAML format";

    public Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        var yaml = @"
# Aurora package metadata
package:
  name: acl
  version: 2.3.2-1
  description: Access control list utilities
  architecture: x86_64
  build_date: 1769866934

metadata:
  license: 
    - LGPL-2.1
  conflicts: 
    - old-acl

dependencies:
  runtime: 
    - glibc
  build: 
    - attr

files:
  package_size: 346398
  source_hash: 7dec4e45e0c8c10b2855f13cd0f5c5bccbb81751f1cecb698beaf1b9f0fe9f60
";
        var pkg = Aurora.Core.Parsing.PackageParser.ParseManifest(yaml);
        
        Spectre.Console.AnsiConsole.MarkupLine($"[green]Parsed Package:[/] {pkg.Name} v{pkg.Version}");
        Spectre.Console.AnsiConsole.MarkupLine($"Build Date: {pkg.BuildDate}");
        Spectre.Console.AnsiConsole.MarkupLine($"Runtime Deps: {string.Join(", ", pkg.Depends)}");
        Spectre.Console.AnsiConsole.MarkupLine($"Build Deps (Ignored): {string.Join(", ", pkg.OptDepends)}"); // Note: Build deps not in Package model usually
        Spectre.Console.AnsiConsole.MarkupLine($"Hash: {pkg.Checksum}");

        if (pkg.Name == "acl" && pkg.Depends.Contains("glibc") && pkg.Checksum.StartsWith("7dec"))
        {
            Spectre.Console.AnsiConsole.MarkupLine("[bold green]PASS: New format parsed successfully.[/]");
        }
        else
        {
            Spectre.Console.AnsiConsole.MarkupLine("[bold red]FAIL: Parsing incorrect.[/]");
        }
        
        return Task.CompletedTask;
    }
}