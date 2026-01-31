using Aurora.Core.Models;

namespace Aurora.Core.Parsing;

/// <summary>
/// Entry point for all package-related parsing logic.
/// Handles the translation from YAML contract to internal Domain Model.
/// </summary>
public static class PackageParser
{
    /// <summary>
    /// Parses a package manifest (aurora.meta) found inside an archive.
    /// </summary>
    public static Package ParseManifest(string content)
    {
        var manifest = ManifestParser.Parse(content);
        return ManifestConverter.ToPackage(manifest);
    }

    /// <summary>
    /// For repositories, we use the specific RepoParser to handle the .aurepo format.
    /// This method is kept for backwards compatibility if we encounter single manifest files.
    /// </summary>
    public static List<Package> ParseRepository(string content)
    {
        // For v1.0, we prefer the .aurepo format handled by RepoParser.Parse()
        // But if someone passes a multi-document manifest file, we handle it here.
        var pkgs = new List<Package>();
        var docs = content.Split(new[] { "---" }, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var doc in docs)
        {
            if (string.IsNullOrWhiteSpace(doc)) continue;
            try { pkgs.Add(ParseManifest(doc)); } catch { /* log and skip */ }
        }
        return pkgs;
    }
}