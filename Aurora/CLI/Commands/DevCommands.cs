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
            
            using var fs = File.Create(path);
            using var gz = new GZipStream(fs, CompressionLevel.Fastest);
            using var tar = new TarWriter(gz, TarEntryFormat.Pax);

            var content = Encoding.UTF8.GetBytes($"Binary for {name}");
            var ms = new MemoryStream(content);
            var entry = new PaxTarEntry(TarEntryType.RegularFile, $"usr/bin/{name}");
            entry.DataStream = ms;
            tar.WriteEntry(entry);

            // NEW: Compute Hash
            var hash = HashHelper.ComputeFileHash(path);
            pkg.Checksum = hash; // Store in object for YAML writing

            AnsiConsole.MarkupLine($"Created [cyan]{filename}[/] (SHA256: {hash.Substring(0, 8)}...)");
        }

        MakePkg("glibc", "2.38", new string[]{}, new string[]{});
        MakePkg("ncurses", "6.4", new []{"glibc"}, new string[]{});
        MakePkg("vim", "9.0", new []{"ncurses"}, new []{"nano"}); 
        MakePkg("nano", "7.2", new []{"ncurses"}, new []{"vim"});

        var sb = new StringBuilder();
        foreach (var p in packages)
        {
            sb.AppendLine($"- name: \"{p.Name}\"");
            sb.AppendLine($"  version: \"{p.Version}\"");
            sb.AppendLine($"  arch: \"{p.Arch}\"");
            if (p.Depends.Any())
            {
                sb.AppendLine("  deps:");
                foreach (var d in p.Depends) sb.AppendLine($"    - {d}");
            }
            if (p.Conflicts.Any())
            {
                sb.AppendLine("  conflicts:");
                foreach (var c in p.Conflicts) sb.AppendLine($"    - {c}");
            }
            if (!string.IsNullOrEmpty(p.Checksum))
            {
                sb.AppendLine($"  sha256: \"{p.Checksum}\"");
            }
            sb.AppendLine();
        }

        File.WriteAllText(Path.Combine(outputDir, "repo.yaml"), sb.ToString());
        
        // --- NEW: Sign the repo ---
        try 
        {
            // We sign the repo.yaml -> repo.yaml.asc
            // We pass null for HomeDir to use the user's default keychain for signing
            // (Or we could accept a flag --gpghome)
            
            AnsiConsole.MarkupLine("[blue]Signing repository...[/]");
            GpgHelper.SignFile(Path.Combine(outputDir, "repo.yaml"));
            AnsiConsole.MarkupLine("[green]Signed repo.yaml.asc generated.[/]");
        }
        catch
        {
            AnsiConsole.MarkupLine("[yellow]Warning: GPG signing failed (Do you have a key?). Repository is unsigned.[/]");
        }
        
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