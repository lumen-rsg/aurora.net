using Aurora.Core.Contract;
using Aurora.Core.Models;

namespace Aurora.Core.Parsing;

public static class RepoParser
{
    private enum Section { None, Metadata, Packages }

    public static AuroraRepoFile Parse(string content)
    {
        var repoFile = new AuroraRepoFile();
        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        Section currentSection = Section.None;
        Package? currentPackage = null;
        List<string>? currentList = null;

        foreach (var rawLine in lines)
        {
            var indent = rawLine.Length - rawLine.TrimStart().Length;
            var line = rawLine.Trim();

            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;

            // --- Level 0: Top-level sections (metadata, packages) ---
            if (indent == 0 && line.EndsWith(":"))
            {
                currentSection = line switch
                {
                    "metadata:" => Section.Metadata,
                    "packages:" => Section.Packages,
                    _ => Section.None
                };
                continue;
            }

            // --- Level 2: Start of a new package ---
            if (currentSection == Section.Packages && indent == 2 && line.StartsWith("- name:"))
            {
                if (currentPackage != null) repoFile.Packages.Add(currentPackage);
                currentPackage = new Package();
                currentList = null;
        
                var parts = line.Substring(2).Split(':', 2);
                // FIX: Added .Trim('\'').Trim('"')
                if(parts.Length == 2) currentPackage.Name = parts[1].Trim().Trim('\'').Trim('"');
                continue;
            }
            
            // --- Level 4/6: Properties within Metadata or Package ---
            if (indent >= 2 && currentSection == Section.Metadata)
            {
                ParseMetadataProperty(repoFile.Metadata, line);
            }
            else if (indent >= 4 && currentSection == Section.Packages && currentPackage != null)
            {
                // Check if this line is the start of a list (e.g., "files:")
                if (line.EndsWith(":"))
                {
                    currentList = GetPackageListProperty(currentPackage, line.TrimEnd(':'));
                }
                // Check if this is an item in the current list
                else if (line.StartsWith("- "))
                {
                    currentList?.Add(line.Substring(2).Trim().Trim('\'').Trim('"'));
                }
                // Otherwise, it's a scalar property
                else
                {
                    currentList = null; // Reset list context
                    ParsePackageProperty(currentPackage, line);
                }
            }
        }

        // Add the last package to the list
        if (currentPackage != null)
        {
            repoFile.Packages.Add(currentPackage);
        }

        return repoFile;
    }

    private static void ParseMetadataProperty(RepoMetadata meta, string line)
    {
        var parts = line.Split(':', 2);
        if (parts.Length < 2) return;
        var key = parts[0].Trim();
        var val = parts[1].Trim();
        switch (key)
        {
            case "creator": meta.Creator = val; break;
            case "timestamp": long.TryParse(val, out var ts); meta.Timestamp = ts; break;
            case "description": meta.Description = val; break;
        }
    }
    
    private static void ParsePackageProperty(Package pkg, string line)
    {
        var parts = line.Split(':', 2);
        if (parts.Length < 2) return;
        var key = parts[0].Trim();
        var val = parts[1].Trim().Trim('\'').Trim('"');
        switch (key)
        {
            case "version": pkg.Version = val; break;
            case "arch": pkg.Arch = val; break;
            case "description": pkg.Description = val; break;
            case "url": pkg.Url = val; break;
            case "build_date": long.TryParse(val, out var bd); pkg.BuildDate = bd; break;
            case "installed_size": long.TryParse(val, out var size); pkg.InstalledSize = size; break;
            case "checksum": pkg.Checksum = val; break;
        }
    }
    
    private static List<string>? GetPackageListProperty(Package pkg, string key)
    {
        return key switch
        {
            "licenses" => pkg.Licenses,
            "provides" => pkg.Provides,
            "conflicts" => pkg.Conflicts,
            "replaces" => pkg.Replaces,
            "depends" => pkg.Depends,
            "files" => pkg.Files,
            _ => null
        };
    }
}