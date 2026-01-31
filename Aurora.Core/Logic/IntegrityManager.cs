using Aurora.Core.Contract;
using Aurora.Core.Models;
using Aurora.Core.Security;
using Spectre.Console;

namespace Aurora.Core.Logic;

public class IntegrityManager
{
    // Added 'startDir' parameter
    public void VerifyChecksums(AuroraManifest manifest, string downloadDir, string startDir)
    {
        var sources = manifest.Build.Source;
        var sums = manifest.Build.Sha256Sums;

        if (sums.Count == 0) return;

        if (sums.Count != sources.Count)
        {
            throw new InvalidDataException(
                $"Integrity Error: Source array has {sources.Count} items, but sha256sums has {sums.Count}. They must match.");
        }

        AnsiConsole.MarkupLine("[bold]Verifying source integrity...[/]");

        for (int i = 0; i < sources.Count; i++)
        {
            var expectedSum = sums[i];
            var sourceStr = sources[i];
            var entry = new SourceEntry(sourceStr);

            // LOGIC FIX: Determine where the file actually is for hashing
            string filePath = entry.Protocol == "local"
                ? Path.Combine(startDir, entry.FileName)
                : Path.Combine(downloadDir, entry.FileName);

            AnsiConsole.Markup($"  {entry.FileName} ... ");

            if (expectedSum == "SKIP")
            {
                AnsiConsole.MarkupLine("[yellow]Skipped[/]");
                continue;
            }

            if (!File.Exists(filePath))
            {
                if (Directory.Exists(filePath))
                {
                    AnsiConsole.MarkupLine("[blue]VCS Repo (Skipped)[/]");
                    continue;
                }
                
                // Print the path we tried to check for easier debugging
                AnsiConsole.MarkupLine($"[red]FAILED (File not found at {filePath})[/]");
                throw new FileNotFoundException($"Source file missing: {entry.FileName}");
            }

            var actualSum = HashHelper.ComputeFileHash(filePath);

            if (!string.Equals(expectedSum, actualSum, StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine("[red]FAILED[/]");
                throw new InvalidDataException(
                    $"Checksum mismatch for {entry.FileName}.\nExpected: {expectedSum}\nActual:   {actualSum}");
            }

            AnsiConsole.MarkupLine("[green]Passed[/]");
        }
    }
}