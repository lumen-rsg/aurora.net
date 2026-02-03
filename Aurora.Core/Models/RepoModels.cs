using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Aurora.Core.Models;

public class Repository
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("packages")]
    public List<RepoPackage> Packages { get; set; } = new();
}

public class RepoPackage
{
    // Identity
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("arch")]
    public string Arch { get; set; } = string.Empty;

    [JsonPropertyName("desc")]
    public string Description { get; set; } = string.Empty;

    // Location & Integrity
    [JsonPropertyName("filename")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long CompressedSize { get; set; }

    [JsonPropertyName("installed_size")]
    public long InstalledSize { get; set; }

    [JsonPropertyName("sha256")]
    public string Checksum { get; set; } = string.Empty;

    // Metadata
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("packager")]
    public string Packager { get; set; } = string.Empty;

    [JsonPropertyName("build_date")]
    public long BuildDate { get; set; }

    // Relationships
    [JsonPropertyName("license")]
    public List<string> License { get; set; } = new();

    [JsonPropertyName("depends")]
    public List<string> Depends { get; set; } = new();

    [JsonPropertyName("provides")]
    public List<string> Provides { get; set; } = new();

    [JsonPropertyName("conflicts")]
    public List<string> Conflicts { get; set; } = new();

    [JsonPropertyName("replaces")]
    public List<string> Replaces { get; set; } = new();
}