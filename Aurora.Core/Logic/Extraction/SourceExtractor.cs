using Aurora.Core.Contract;
using Aurora.Core.Models;
using Spectre.Console;

namespace Aurora.Core.Logic.Extraction;

public class SourceExtractor
{
    private readonly List<IExtractionProvider> _providers = new()
    {
        new ArchiveExtractionProvider(),
        new VcsExtractionProvider()
    };

    public async Task ExtractAllAsync(AuroraManifest manifest, string cacheDir, string srcDir)
    {
        AnsiConsole.MarkupLine("[bold]Extracting sources...[/]");

        foreach (var sourceStr in manifest.Build.Source)
        {
            var entry = new SourceEntry(sourceStr);
            var cachePath = Path.Combine(cacheDir, entry.FileName);

            // Skip if in noextract array
            if (manifest.Build.NoExtract.Contains(entry.FileName))
            {
                AnsiConsole.MarkupLine($"  {entry.FileName} ... [yellow]Skipped (noextract)[/]");
                
                // Still symlink it!
                var targetLink = Path.Combine(srcDir, entry.FileName);
                if (File.Exists(targetLink)) File.Delete(targetLink);
                File.CreateSymbolicLink(targetLink, cachePath);
                continue;
            }

            var provider = _providers.FirstOrDefault(p => p.CanHandle(entry));
            if (provider != null)
            {
                await provider.ExtractAsync(entry, cachePath, srcDir, msg => 
                {
                    AnsiConsole.MarkupLine($"  [grey]-> {msg}[/]");
                });
            }
        }
    }
}