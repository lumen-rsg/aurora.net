namespace Aurora.Core.Contract;

public class RepoConfig
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    // Standard RPM repository URL sources (at least one should be set)
    public string BaseUrl { get; set; } = string.Empty;
    public string Metalink { get; set; } = string.Empty;
    public string Mirrorlist { get; set; } = string.Empty;

    // Policy
    public bool Enabled { get; set; } = true;
    public bool GpgCheck { get; set; } = false;
    public string GpgKey { get; set; } = string.Empty;
    public int Priority { get; set; } = 99;
    public int Cost { get; set; } = 1000;

    // Package filtering
    public string Exclude { get; set; } = string.Empty;
    public string IncludePkgs { get; set; } = string.Empty;

    // Tracks which .repo file this entry was parsed from (for write-back)
    public string? SourceFile { get; set; }

    // Set after mirror resolution at sync time, used by RepoManager
    public string EffectiveUrl { get; set; } = string.Empty;

    public bool HasMirrors => !string.IsNullOrEmpty(Metalink) || !string.IsNullOrEmpty(Mirrorlist);

    // Helper property to maintain compatibility with existing RepoManager logic
    public string Url => !string.IsNullOrEmpty(EffectiveUrl) ? EffectiveUrl : BaseUrl;
}
