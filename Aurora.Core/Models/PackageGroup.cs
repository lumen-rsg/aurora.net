using System.Text.Json.Serialization;

namespace Aurora.Core.Models;

/// <summary>
///     Represents an RPM comps group — a named collection of packages
///     (e.g., "Development Tools", "Web Server", "GNOME Desktop Environment").
/// </summary>
public class PackageGroup
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("display_order")]
    public int DisplayOrder { get; set; }

    [JsonPropertyName("is_default")]
    public bool IsDefault { get; set; }

    [JsonPropertyName("user_visible")]
    public bool Uservisible { get; set; } = true;

    [JsonPropertyName("packages")]
    public List<GroupPackage> Packages { get; set; } = new();

    /// <summary>
    ///     The repository this group was loaded from.
    /// </summary>
    [JsonPropertyName("repo_id")]
    public string RepoId { get; set; } = string.Empty;

    /// <summary>
    ///     Returns mandatory + default packages (what `group install` installs by default).
    /// </summary>
    public IEnumerable<GroupPackage> DefaultPackages =>
        Packages.Where(p => p.Type is GroupPackageType.Mandatory or GroupPackageType.Default);
}

/// <summary>
///     A single package entry within a comps group, with its installation type.
/// </summary>
public class GroupPackage
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public GroupPackageType Type { get; set; } = GroupPackageType.Default;
}

/// <summary>
///     The installation priority of a package within a group.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GroupPackageType
{
    Mandatory,
    Default,
    Optional,
    Conditional
}

/// <summary>
///     Represents an RPM comps category — a collection of groups
///     (e.g., "Development" containing "Development Tools" and "C Development").
/// </summary>
public class PackageCategory
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("display_order")]
    public int DisplayOrder { get; set; }

    [JsonPropertyName("group_ids")]
    public List<string> GroupIds { get; set; } = new();

    [JsonPropertyName("repo_id")]
    public string RepoId { get; set; } = string.Empty;
}