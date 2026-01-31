using Aurora.Core.Contract;
using Aurora.Core.Models;

namespace Aurora.Core.Parsing;

public static class ManifestConverter
{
    public static Package ToPackage(AuroraManifest manifest)
    {
        return new Package
        {
            // Core
            Name = manifest.Package.Name,
            Version = manifest.Package.Version,
            Arch = manifest.Package.Architecture,
            Description = manifest.Package.Description,
            
            // Metadata
            Maintainer = manifest.Package.Maintainer,
            Url = manifest.Metadata.Url,
            Licenses = manifest.Metadata.License,
            BuildDate = manifest.Package.BuildDate,

            // Dependencies 
            Depends = manifest.Dependencies.Runtime,
            OptDepends = manifest.Dependencies.Optional,
            MakeDepends = manifest.Dependencies.Build,
            Conflicts = manifest.Metadata.Conflicts,
            Replaces = manifest.Metadata.Replaces,
            Provides = manifest.Metadata.Provides,
            Backup = manifest.Metadata.Backup,

            // Files & Security
            InstalledSize = manifest.Files.PackageSize,
            Checksum = manifest.Files.SourceHash,
            
            // Defaults
            InstallReason = "explicit",
            IsBroken = false
        };
    }
}