using System.Formats.Tar;
using System.IO.Compression;
using Aurora.Core.Models;
using Aurora.Core.Parsing;

namespace Aurora.Core.IO;

public static class PackageExtractor
{
    public static Package ReadManifest(string packagePath)
    {
        if (!File.Exists(packagePath)) 
            throw new FileNotFoundException($"Package file not found: {packagePath}");

        using var fs = File.OpenRead(packagePath);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var tar = new TarReader(gz);

        while (tar.GetNextEntry() is { } entry)
        {
            // Normalize path separators
            var name = entry.Name.Replace('\\', '/');
            if (name.StartsWith("./")) name = name.Substring(2);

            // Support both new strict format and legacy format
            if (name == "aurora.meta" || name == ".AURORA_META")
            {
                using var stream = entry.DataStream;
                if (stream == null) throw new InvalidDataException("Manifest is empty");

                using var reader = new StreamReader(stream);
                var content = reader.ReadToEnd();
                
                // Use our robust parser
                return PackageParser.ParseManifest(content);
            }
        }

        throw new InvalidDataException("Package is missing 'aurora.meta' or '.AURORA_META'");
    }
    
    public static List<string> GetFileList(string packagePath)
    {
        var files = new List<string>();
        using var fs = File.OpenRead(packagePath);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var tar = new TarReader(gz);

        while (tar.GetNextEntry() is { } entry)
        {
            var name = entry.Name.Replace('\\', '/');
            if (name.StartsWith("./")) name = name.Substring(2);

            // Ignore the same metadata files we ignore during install
            var fileName = Path.GetFileName(name);
            if (string.IsNullOrEmpty(fileName) || name.Contains(".AURORA_") || name == "aurora.meta" || name == ".INSTALL")
                continue;

            files.Add("/" + name.TrimStart('/'));
        }
        return files;
    }
}