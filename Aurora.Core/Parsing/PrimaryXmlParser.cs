using System.Collections.Generic;
using System.IO;
using System.Xml;
using Aurora.Core.Models;

namespace Aurora.Core.Parsing;

public static class PrimaryXmlParser
{
    public static List<Package> ParseFile(string xmlPath, string repoId)
    {
        var packages = new List<Package>();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true };

        using var reader = XmlReader.Create(xmlPath, settings);

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "package")
            {
                var pkg = ParsePackage(reader, repoId);
                if (pkg != null)
                    packages.Add(pkg);
            }
        }

        return packages;
    }

    public static List<Package> Parse(string xmlContent, string repoId)
    {
        var packages = new List<Package>();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true };

        using var reader = XmlReader.Create(new StringReader(xmlContent), settings);

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "package")
            {
                var pkg = ParsePackage(reader, repoId);
                if (pkg != null)
                    packages.Add(pkg);
            }
        }

        return packages;
    }

    private static Package? ParsePackage(XmlReader reader, string repoId)
    {
        var pkg = new Package { RepositoryId = repoId };
        using var subtree = reader.ReadSubtree();

        while (subtree.Read())
        {
            if (subtree.NodeType != XmlNodeType.Element) continue;

            switch (subtree.LocalName)
            {
                case "name":
                    pkg.Name = subtree.ReadElementContentAsString().Trim();
                    break;
                case "arch":
                    pkg.Arch = subtree.ReadElementContentAsString().Trim();
                    break;
                case "version":
                    pkg.Epoch = subtree.GetAttribute("epoch") ?? "0";
                    pkg.Version = subtree.GetAttribute("ver") ?? "";
                    pkg.Release = subtree.GetAttribute("rel") ?? "";
                    if (!subtree.IsEmptyElement) subtree.ReadElementContentAsString();
                    break;
                case "checksum":
                    pkg.ChecksumType = subtree.GetAttribute("type") ?? "sha256";
                    pkg.Checksum = subtree.ReadElementContentAsString().Trim();
                    break;
                case "summary":
                    pkg.Summary = subtree.ReadElementContentAsString().Trim();
                    break;
                case "description":
                    pkg.Description = subtree.ReadElementContentAsString().Trim();
                    break;
                case "url":
                    pkg.Url = subtree.ReadElementContentAsString().Trim();
                    break;
                case "size":
                    var packageAttr = subtree.GetAttribute("package");
                    var installedAttr = subtree.GetAttribute("installed");
                    if (packageAttr != null && long.TryParse(packageAttr, out var pkgSize))
                        pkg.Size = pkgSize;
                    if (installedAttr != null && long.TryParse(installedAttr, out var instSize))
                        pkg.InstalledSize = instSize;
                    if (!subtree.IsEmptyElement) subtree.ReadElementContentAsString();
                    break;
                case "location":
                    pkg.LocationHref = subtree.GetAttribute("href") ?? "";
                    if (!subtree.IsEmptyElement) subtree.ReadElementContentAsString();
                    break;
                case "format":
                    ParseFormat(subtree, pkg);
                    break;
            }
        }

        return string.IsNullOrEmpty(pkg.Name) ? null : pkg;
    }

    private static void ParseFormat(XmlReader reader, Package pkg)
    {
        var depth = reader.Depth;
        if (reader.IsEmptyElement) return;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                break;

            if (reader.NodeType != XmlNodeType.Element) continue;

            switch (reader.LocalName)
            {
                case "license":
                    pkg.License = reader.ReadElementContentAsString().Trim();
                    break;
                case "requires":
                    ReadDeps(reader, pkg.Requires);
                    break;
                case "provides":
                    ReadDeps(reader, pkg.Provides);
                    break;
                case "conflicts":
                    ReadDeps(reader, pkg.Conflicts);
                    break;
                case "obsoletes":
                    ReadDeps(reader, pkg.Obsoletes);
                    break;
                case "recommends":
                    ReadDeps(reader, pkg.Recommends);
                    break;
            }
        }
    }

    private static void ReadDeps(XmlReader reader, List<string> target)
    {
        var depth = reader.Depth;
        if (reader.IsEmptyElement) return;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                break;

            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "entry")
                continue;

            var name = reader.GetAttribute("name") ?? "";
            if (string.IsNullOrEmpty(name)) continue;
            if (name.StartsWith("rpmlib(")) continue;

            var flags = reader.GetAttribute("flags");
            var epoch = reader.GetAttribute("epoch");
            var ver = reader.GetAttribute("ver");
            var rel = reader.GetAttribute("rel");

            target.Add(FormatCapability(name, flags, epoch, ver, rel));
        }
    }

    private static string FormatCapability(string name, string? flags, string? epoch, string? version, string? release)
    {
        if (string.IsNullOrEmpty(flags) || string.IsNullOrEmpty(version))
            return name;

        string op = flags switch
        {
            "EQ" => "=",
            "LT" => "<",
            "GT" => ">",
            "LE" => "<=",
            "GE" => ">=",
            _ => flags
        };

        string fullVersion = version;
        if (!string.IsNullOrEmpty(release))
            fullVersion = $"{version}-{release}";
        if (!string.IsNullOrEmpty(epoch) && epoch != "0")
            fullVersion = $"{epoch}:{fullVersion}";

        return $"{name} {op} {fullVersion}";
    }
}
