using Aurora.Core.Contract;

namespace Aurora.Core.Parsing;

public static class PkgInfoParser
{
    public static AuroraManifest Parse(string content)
    {
        var manifest = new AuroraManifest();
        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            if (line.StartsWith("#") || string.IsNullOrWhiteSpace(line)) continue;

            var parts = line.Split(" = ", 2);
            if (parts.Length != 2) continue;

            var key = parts[0].Trim();
            var value = parts[1].Trim();

            switch (key)
            {
                case "pkgname": manifest.Package.Name = value; break;
                case "pkgver": manifest.Package.Version = value; break;
                case "pkgdesc": manifest.Package.Description = value; break;
                case "url": manifest.Metadata.Url = value; break;
                case "arch": manifest.Package.Architecture = value; break;
                case "packager": manifest.Package.Maintainer = value; break;
                case "builddate": 
                    if (long.TryParse(value, out var bd)) manifest.Package.BuildDate = bd; 
                    break;
                case "size": 
                    if (long.TryParse(value, out var sz)) manifest.Files.PackageSize = sz; 
                    break;
                
                // Arrays
                case "license": manifest.Metadata.License.Add(value); break;
                case "group": manifest.Metadata.Groups.Add(value); break;
                case "depend": manifest.Dependencies.Runtime.Add(value); break;
                case "optdepend": manifest.Dependencies.Optional.Add(value); break;
                case "makedepend": manifest.Dependencies.Build.Add(value); break;
                case "conflict": manifest.Metadata.Conflicts.Add(value); break;
                case "provides": manifest.Metadata.Provides.Add(value); break;
                case "replaces": manifest.Metadata.Replaces.Add(value); break;
                case "backup": manifest.Metadata.Backup.Add(value); break;
            }
        }

        return manifest;
    }
}