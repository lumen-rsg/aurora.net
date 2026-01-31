using Aurora.Core.Contract;

namespace Aurora.Core.Logic;

public interface IBuildProvider
{
    string FormatName { get; }
    
    // Check if this provider can handle the files in the directory
    bool CanHandle(string directory);

    // Phase 1: Metadata extraction
    Task<AuroraManifest> GetManifestAsync(string directory);

    // Phase 2: Source handling (download/checksum)
    Task FetchSourcesAsync(AuroraManifest manifest, string downloadDir, bool skipGpg); 

    // Phase 3: The actual build execution
    Task BuildAsync(AuroraManifest manifest, string srcDir, string pkgDir, Action<string> logAction);
}