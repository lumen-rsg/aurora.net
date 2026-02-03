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
            if (string.IsNullOrWhiteSpace(sourceStr)) continue;
            var entry = new SourceEntry(sourceStr);
            
            string actualLocation = entry.Protocol == "local" 
                ? Path.Combine(startDir, entry.FileName) 
                : Path.Combine(cacheDir, entry.FileName);

            var absoluteSourcePath = Path.GetFullPath(actualLocation);
            var targetLinkInSrc = Path.Combine(srcDir, entry.FileName);

            if (!File.Exists(absoluteSourcePath) && !Directory.Exists(absoluteSourcePath))
                throw new FileNotFoundException($"Source asset not found: {absoluteSourcePath}");

            // Case 1: Manual noextract
            if (manifest.Build.NoExtract.Contains(entry.FileName))
            {
                AnsiConsole.MarkupLine($"  {entry.FileName} ... [yellow]Skipped (noextract)[/]");
                CreateSymlink(absoluteSourcePath, targetLinkInSrc);
                continue;
            }

            var provider = _providers.FirstOrDefault(p => p.CanHandle(entry));

            if (provider == null)
            {
                // Case 2: Plain files (patches, .conf, etc.) -> Symlink them into src/
                CreateSymlink(absoluteSourcePath, targetLinkInSrc);
            }
            else
            {
                // Case 3: Archives and VCS -> Let provider handle it.
                // Providers create their own directories or extract content.
                // They do NOT create a file-symlink with the alias name.
                await provider.ExtractAsync(entry, absoluteSourcePath, srcDir, msg => {
                    AnsiConsole.MarkupLine($"  [grey]-> {msg}[/]");
                });
            }
        }
    }

    private void CreateSymlink(string source, string target)
    {
        if (File.Exists(target) || Directory.Exists(target)) 
        {
            if (Path.GetFullPath(target) == Path.GetFullPath(source)) return;
            try { File.Delete(target); } catch { Directory.Delete(target, true); }
        }
        File.CreateSymbolicLink(target, source);
    }
}