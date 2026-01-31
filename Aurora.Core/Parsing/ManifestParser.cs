using Aurora.Core.Contract;
using Aurora.Core.Models;

namespace Aurora.Core.Parsing;

public static class ManifestParser
{
    private enum Section { None, Package, Metadata, Dependencies, Build, Files }

    public static AuroraManifest Parse(string content)
    {
        var manifest = new AuroraManifest();
        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        Section currentSection = Section.None;
        List<string>? currentList = null; // Pointer to the active list being filled

        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;
            
            // Calculate Indentation
            int indent = 0;
            while (indent < rawLine.Length && rawLine[indent] == ' ') indent++;
            
            var line = rawLine.Trim();
            if (line.StartsWith("#")) continue; // Skip comments

            // --- LEVEL 0: ROOT SECTIONS ---
            if (indent == 0 && line.EndsWith(":"))
            {
                currentList = null; // Reset list context
                var key = line.TrimEnd(':');
                switch (key)
                {
                    case "package": currentSection = Section.Package; break;
                    case "metadata": currentSection = Section.Metadata; break;
                    case "dependencies": currentSection = Section.Dependencies; break;
                    case "build": currentSection = Section.Build; break;
                    case "files": currentSection = Section.Files; break;
                    default: currentSection = Section.None; break;
                }
                continue;
            }

            // --- LIST ITEM HANDLING ---
            if (line.StartsWith("- "))
            {
                if (currentList != null)
                {
                    // FIX: Trim spaces AND quotes
                    var value = line.Substring(2).Trim().Trim('"').Trim('\'');
                    if (!string.IsNullOrEmpty(value)) 
                    {
                        currentList.Add(value);
                    }
                }
                continue;
            }

            // --- LEVEL 1 & 2: PROPERTIES ---
            // Expect format "key: value" OR "key:" (start of list)
            var parts = line.Split(':', 2);
            var keyName = parts[0].Trim();
            var valPart = parts.Length > 1 ? parts[1].Trim().Trim('"').Trim('\'') : "";

            // If value is empty, this might be the start of a list (e.g. "runtime:")
            if (string.IsNullOrEmpty(valPart))
            {
                // Map the key to the specific list in the current section
                currentList = GetListProperty(manifest, currentSection, keyName);
                continue;
            }

            // Otherwise, it's a scalar value (e.g. "name: acl")
            currentList = null; // Reset list context as we hit a scalar
            AssignScalar(manifest, currentSection, keyName, valPart);
        }

        return manifest;
    }

    private static List<string>? GetListProperty(AuroraManifest m, Section section, string key)
    {
        switch (section)
        {
            case Section.Metadata:
                return key switch
                {
                    "license" => m.Metadata.License,
                    "groups" => m.Metadata.Groups,
                    "provides" => m.Metadata.Provides,
                    "conflicts" => m.Metadata.Conflicts,
                    "replaces" => m.Metadata.Replaces,
                    "backup" => m.Metadata.Backup,
                    _ => null
                };
            case Section.Dependencies:
                return key switch
                {
                    "runtime" => m.Dependencies.Runtime,
                    "optional" => m.Dependencies.Optional,
                    "build" => m.Dependencies.Build,
                    "test" => m.Dependencies.Test,
                    _ => null
                };
            case Section.Build:
                return key switch
                {
                    "environment" => m.Build.Environment,
                    "options" => m.Build.Options,
                    "source" => m.Build.Source,       // <--- NEW
                    "sha256sums" => m.Build.Sha256Sums, // <--- NEW
                    "noextract" => m.Build.NoExtract,
                    _ => null
                };
        }
        return null;
    }

    private static void AssignScalar(AuroraManifest m, Section section, string key, string value)
    {
        switch (section)
        {
            case Section.Package:
                switch (key)
                {
                    case "name": m.Package.Name = value; break;
                    case "version": m.Package.Version = value; break;
                    case "description": m.Package.Description = value; break;
                    case "architecture": m.Package.Architecture = value; break;
                    case "maintainer": m.Package.Maintainer = value; break;
                    case "build_date": long.TryParse(value, out var bd); m.Package.BuildDate = bd; break;
                    case "build_tool": m.Package.BuildTool = value; break;
                    case "build_tool_version": m.Package.BuildToolVersion = value; break;
                }
                break;

            case Section.Metadata:
                switch (key)
                {
                    case "pkgname": m.Metadata.PkgName = value; break;
                    case "url": m.Metadata.Url = value; break;
                }
                break;

            case Section.Build:
                switch (key)
                {
                    case "directory": m.Build.Directory = value; break;
                    case "source_directory": m.Build.SourceDirectory = value; break;
                }
                break;

            case Section.Files:
                switch (key)
                {
                    case "package_size": long.TryParse(value, out var ps); m.Files.PackageSize = ps; break;
                    case "source_hash": m.Files.SourceHash = value; break;
                }
                break;
        }
    }
}