using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace Aurora.Core.Parsing;

public static class FilelistsXmlParser
{
    // Only extract file paths matching these prefixes (same filter as RpmRepoDb)
    private static readonly string[] ImportantPrefixes =
    {
        "/usr/bin/", "/bin/", "/usr/sbin/", "/sbin/", "/etc/"
    };

    public static Dictionary<string, List<string>> ParseFile(string xmlPath)
    {
        var result = new Dictionary<string, List<string>>();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true };

        using var reader = XmlReader.Create(xmlPath, settings);

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "package")
            {
                var pkgId = reader.GetAttribute("pkgid") ?? "";
                if (string.IsNullOrEmpty(pkgId)) continue;

                var files = ParsePackageFiles(reader);
                if (files.Count > 0)
                    result[pkgId] = files;
            }
        }

        return result;
    }

    public static Dictionary<string, List<string>> Parse(string xmlContent)
    {
        var result = new Dictionary<string, List<string>>();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true };

        using var reader = XmlReader.Create(new StringReader(xmlContent), settings);

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "package")
            {
                var pkgId = reader.GetAttribute("pkgid") ?? "";
                if (string.IsNullOrEmpty(pkgId)) continue;

                var files = ParsePackageFiles(reader);
                if (files.Count > 0)
                    result[pkgId] = files;
            }
        }

        return result;
    }

    private static List<string> ParsePackageFiles(XmlReader reader)
    {
        var files = new List<string>();
        var depth = reader.Depth;

        if (reader.IsEmptyElement) return files;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                break;

            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "file")
                continue;

            // Skip directories and ghost files
            var type = reader.GetAttribute("type");
            if (type == "dir" || type == "ghost") continue;

            var path = reader.ReadElementContentAsString().Trim();
            if (!IsImportantFile(path)) continue;

            files.Add(path);
        }

        return files;
    }

    private static bool IsImportantFile(string path)
    {
        foreach (var prefix in ImportantPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
