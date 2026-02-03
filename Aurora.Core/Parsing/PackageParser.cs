using Aurora.Core.Models;

namespace Aurora.Core.Parsing;

public static class PackageParser
{
    /// <summary>
    /// Parses standard Arch .PKGINFO content into our internal Package model.
    /// </summary>
    public static Package ParsePkgInfo(string content)
    {
        // 1. Parse raw Key-Value text into the Manifest Object
        var manifest = PkgInfoParser.Parse(content);
        
        // 2. Convert to Internal Domain Model (Package)
        return ManifestConverter.ToPackage(manifest);
    }
}