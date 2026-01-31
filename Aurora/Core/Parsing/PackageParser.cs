using Aurora.Core.Models;

namespace Aurora.Core.Parsing;

public static class PackageParser
{
    public static Package ParseManifest(string content)
    {
        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        var pkg = new Package();
        ParseBlock(lines, 0, lines.Length, pkg);
        return pkg;
    }

    public static List<Package> ParseRepository(string content)
    {
        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        var packages = new List<Package>();
        
        Package? currentPkg = null;
        var currentBlockStart = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            
            if (line.StartsWith("- name:"))
            {
                if (currentPkg != null)
                {
                    ParseBlock(lines, currentBlockStart, i, currentPkg);
                    packages.Add(currentPkg);
                }

                currentPkg = new Package();
                currentBlockStart = i; 
            }
        }

        if (currentPkg != null)
        {
            ParseBlock(lines, currentBlockStart, lines.Length, currentPkg);
            packages.Add(currentPkg);
        }

        return packages;
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