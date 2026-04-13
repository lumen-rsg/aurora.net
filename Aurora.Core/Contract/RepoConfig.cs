namespace Aurora.Core.Contract;

public class RepoConfig
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    
    // Standard RPM repository keys
    public string BaseUrl { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool GpgCheck { get; set; } = false;
    public string GpgKey { get; set; } = string.Empty;

    // Tracks which .repo file this entry was parsed from (for write-back)
    public string? SourceFile { get; set; }

    // Helper property to maintain compatibility with existing RepoManager logic
    public string Url => BaseUrl;
}
