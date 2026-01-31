using Aurora.Core.Models;

namespace Aurora.Core.Parsing;

public static class PackageParser
{
    public static Package ParseManifest(string content)
    {
        // 1. Parse into strict Contract
        var manifest = ManifestParser.Parse(content);
        
        // 2. Convert to Domain Model
        return ManifestConverter.ToPackage(manifest);
    }

    public static List<Package> ParseRepository(string content)
    {
        // If repo.yaml format is a list of these full blocks, we need a slight adjustment.
        // But typically repo.yaml is a summarised version.
        // Let's assume for this step we are parsing the 'aurora.meta' inside a package.
        
        // If you need to parse a list of these using the new format:
        var pkgs = new List<Package>();
        // Simple splitter for YAML documents if separated by dashes
        var docs = content.Split(new[] { "---" }, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var doc in docs)
        {
            if (string.IsNullOrWhiteSpace(doc)) continue;
            try 
            {
                pkgs.Add(ParseManifest(doc));
            }
            catch { /* warning */ }
        }
        return pkgs;
    }

    private static void ParseBlock(string[] lines, int start, int end, Package pkg)
    {
        string? currentListProperty = null;

        for (int i = start; i < end; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#")) continue;

            bool isNewObjectKey = trimmed.StartsWith("- name:");
            
            if (!isNewObjectKey && trimmed.StartsWith("-"))
            {
                if (currentListProperty != null)
                {
                    var val = CleanValue(trimmed.Substring(1));
                    AddToList(pkg, currentListProperty, val);
                }
                continue;
            }

            var parts = trimmed.Split(':', 2);
            if (parts.Length < 2) continue;

            // FIX: Ensure we trim after removing the dash
            var key = parts[0].Trim().TrimStart('-').Trim();
            var value = parts[1].Trim();

            if (value.StartsWith("[") && value.EndsWith("]"))
            {
                currentListProperty = null; 
                var inner = value.Substring(1, value.Length - 2); 
                var items = inner.Split(',');
                foreach (var item in items)
                {
                    if(!string.IsNullOrWhiteSpace(item))
                        AddToList(pkg, key, CleanValue(item));
                }
                continue;
            }

            if (string.IsNullOrEmpty(value))
            {
                currentListProperty = key;
                continue;
            }

            currentListProperty = null; 
            AssignProperty(pkg, key, CleanValue(value));
        }
    }

    private static string CleanValue(string raw)
    {
        return raw.Trim().Trim('"', '\'');
    }

    private static void AssignProperty(Package pkg, string key, string value)
    {
        switch (key)
        {
            case "name": pkg.Name = value; break;
            case "version": pkg.Version = value; break;
            case "arch": pkg.Arch = value; break;
            case "description": pkg.Description = value; break;
            case "sha256": pkg.Checksum = value; break; // <--- NEW
        }
    }

    private static void AddToList(Package pkg, string key, string value)
    {
        switch (key)
        {
            case "deps": pkg.Depends.Add(value); break;
            case "makedepends": pkg.MakeDepends.Add(value); break;
            case "conflicts": pkg.Conflicts.Add(value); break;
            case "replaces": pkg.Replaces.Add(value); break;
            case "provides": pkg.Provides.Add(value); break;
            case "files": pkg.Files.Add(value); break;
        }
    }
    
    
}