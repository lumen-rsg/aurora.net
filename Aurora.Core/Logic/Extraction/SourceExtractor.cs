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

    public async Task ExtractAllAsync(AuroraManifest manifest, string cacheDir, string srcDir, string startDir)
    {
        AnsiConsole.MarkupLine("[bold]Extracting sources...[/]");

        foreach (var sourceStr in manifest.Build.Source)
        {
            var entry = new SourceEntry(sourceStr);
            
            // 1. Resolve where the file actually lives right now.
            // Local files are in the project folder (startDir).
            // Remote files have been downloaded to the cache folder (cacheDir).
            string actualLocation = entry.Protocol == "local" 
                ? Path.Combine(startDir, entry.FileName) 
                : Path.Combine(cacheDir, entry.FileName);

            var absoluteSourcePath = Path.GetFullPath(actualLocation);
            var targetLink = Path.Combine(srcDir, entry.FileName);

            // 2. Safety Check: Does the source actually exist?
            if (!File.Exists(absoluteSourcePath) && !Directory.Exists(absoluteSourcePath))
            {
                throw new FileNotFoundException($"Source asset not found: {absoluteSourcePath}");
            }

            // 3. Skip extraction if in noextract array
            if (manifest.Build.NoExtract.Contains(entry.FileName))
            {
                AnsiConsole.MarkupLine($"  {entry.FileName} ... [yellow]Skipped (noextract)[/]");
                if (File.Exists(targetLink) || Directory.Exists(targetLink)) File.Delete(targetLink);
                File.CreateSymbolicLink(targetLink, absoluteSourcePath);
                continue;
            }

            // 4. Extract or Symlink
            var provider = _providers.FirstOrDefault(p => p.CanHandle(entry));
            if (provider == null)
            {
                // It's not a known archive or VCS repo (like a .pam or .patch file)
                // Just symlink it into the build srcdir.
                if (File.Exists(targetLink) || Directory.Exists(targetLink)) File.Delete(targetLink);
                File.CreateSymbolicLink(targetLink, absoluteSourcePath);
            }
            else
            {
                // It's an archive or VCS repo, let the provider handle it
                // We pass the absoluteSourcePath (the truth) to the provider
                await provider.ExtractAsync(entry, absoluteSourcePath, srcDir, msg => {
                    AnsiConsole.MarkupLine($"  [grey]-> {msg}[/]");
                });
            }
        }
    }
}