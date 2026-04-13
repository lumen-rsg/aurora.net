using System.Xml;
using System.IO;
using Aurora.Core.Models;

namespace Aurora.Core.Parsing;

/// <summary>
///     Parses RPM comps.xml files — the standard format for package groups
///     and categories in RPM-based repositories.
/// </summary>
public static class CompsParser
{
    /// <summary>
    ///     Parses a comps.xml document and returns all groups and categories.
    /// </summary>
    public static (List<PackageGroup> Groups, List<PackageCategory> Categories) Parse(string xmlContent)
    {
        var groups = new List<PackageGroup>();
        var categories = new List<PackageCategory>();

        using var reader = XmlReader.Create(new StringReader(xmlContent));

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;

            if (reader.Name == "group")
            {
                var group = ParseGroup(reader);
                if (group != null) groups.Add(group);
            }
            else if (reader.Name == "category")
            {
                var category = ParseCategory(reader);
                if (category != null) categories.Add(category);
            }
        }

        return (groups, categories);
    }

    private static PackageGroup? ParseGroup(XmlReader reader)
    {
        var group = new PackageGroup();
        var subtree = reader.ReadSubtree();

        while (subtree.Read())
        {
            if (subtree.NodeType != XmlNodeType.Element) continue;

            switch (subtree.Name)
            {
                case "id":
                    group.Id = subtree.ReadElementContentAsString().Trim();
                    break;
                case "name":
                    group.Name = ParseLocalizedString(subtree);
                    break;
                case "description":
                    group.Description = ParseLocalizedString(subtree);
                    break;
                case "display_order":
                    if (int.TryParse(subtree.ReadElementContentAsString().Trim(), out var order))
                        group.DisplayOrder = order;
                    break;
                case "default":
                    group.IsDefault = ParseBool(subtree.ReadElementContentAsString().Trim());
                    break;
                case "uservisible":
                    group.Uservisible = ParseBool(subtree.ReadElementContentAsString().Trim());
                    break;
                case "packagelist":
                    group.Packages = ParsePackageList(subtree);
                    break;
            }
        }

        subtree.Close();
        return string.IsNullOrEmpty(group.Id) ? null : group;
    }

    private static List<GroupPackage> ParsePackageList(XmlReader reader)
    {
        var packages = new List<GroupPackage>();
        var depth = reader.Depth;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "packagelist")
                break;

            if (reader.NodeType != XmlNodeType.Element) continue;

            if (reader.Name == "packagereq")
            {
                var typeAttr = reader.GetAttribute("type") ?? "default";
                var pkgName = reader.ReadElementContentAsString().Trim();

                if (string.IsNullOrEmpty(pkgName)) continue;

                packages.Add(new GroupPackage
                {
                    Name = pkgName,
                    Type = typeAttr.ToLowerInvariant() switch
                    {
                        "mandatory" => GroupPackageType.Mandatory,
                        "optional" => GroupPackageType.Optional,
                        "conditional" => GroupPackageType.Conditional,
                        _ => GroupPackageType.Default
                    }
                });
            }
        }

        return packages;
    }

    private static PackageCategory? ParseCategory(XmlReader reader)
    {
        var category = new PackageCategory();
        var subtree = reader.ReadSubtree();

        while (subtree.Read())
        {
            if (subtree.NodeType != XmlNodeType.Element) continue;

            switch (subtree.Name)
            {
                case "id":
                    category.Id = subtree.ReadElementContentAsString().Trim();
                    break;
                case "name":
                    category.Name = ParseLocalizedString(subtree);
                    break;
                case "description":
                    category.Description = ParseLocalizedString(subtree);
                    break;
                case "display_order":
                    if (int.TryParse(subtree.ReadElementContentAsString().Trim(), out var order))
                        category.DisplayOrder = order;
                    break;
                case "grouplist":
                    category.GroupIds = ParseGroupList(subtree);
                    break;
            }
        }

        subtree.Close();
        return string.IsNullOrEmpty(category.Id) ? null : category;
    }

    private static List<string> ParseGroupList(XmlReader reader)
    {
        var groupIds = new List<string>();

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "grouplist")
                break;

            if (reader.NodeType != XmlNodeType.Element) continue;

            if (reader.Name == "groupid")
            {
                var id = reader.ReadElementContentAsString().Trim();
                if (!string.IsNullOrEmpty(id))
                    groupIds.Add(id);
            }
        }

        return groupIds;
    }

    /// <summary>
    ///     Parses a localized string element. If there are multiple translations,
    ///     prefers the first one (typically English in Fedora/RHEL comps).
    /// </summary>
    private static string ParseLocalizedString(XmlReader reader)
    {
        if (reader.IsEmptyElement) return string.Empty;
        return reader.ReadElementContentAsString().Trim();
    }

    private static bool ParseBool(string value)
    {
        return value.Equals("true", StringComparison.OrdinalIgnoreCase)
               || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
               || value == "1";
    }
}