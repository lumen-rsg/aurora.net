using Aurora.Core.Contract;
using Aurora.Core.Models;
using Aurora.Core.Security;
using Spectre.Console;

namespace Aurora.Core.Logic;

public class IntegrityManager
{
    public void VerifyChecksums(AuroraManifest manifest, string downloadDir)
    {
        var sources = manifest.Build.Source;
        var sums = manifest.Build.Sha256Sums;

        // If no checksums defined, we skip (or warn in strict mode)
        if (sums.Count == 0) return;

        // Strict array alignment check
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
            var filePath = Path.Combine(downloadDir, entry.FileName);

            AnsiConsole.Markup($"  {entry.FileName} ... ");

            // Logic from verify_checksum.sh: Handle 'SKIP'
            if (expectedSum == "SKIP")
            {
                AnsiConsole.MarkupLine("[yellow]Skipped[/]");
                continue;
            }

            // Verify file existence
            if (!File.Exists(filePath))
            {
                // If it's a directory (VCS repo), we skip hashing for now 
                // (makepkg usually uses SKIP for VCS, but just in case)
                // TODO
                if (Directory.Exists(filePath))
                {
                    AnsiConsole.MarkupLine("[blue]VCS Repo (Skipped)[/]");
                    continue;
                }
                
                AnsiConsole.MarkupLine("[red]FAILED (File not found)[/]");
                throw new FileNotFoundException($"Source file missing: {entry.FileName}");
            }

            // Compute Hash
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