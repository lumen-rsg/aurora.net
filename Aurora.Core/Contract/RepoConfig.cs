namespace Aurora.Core.Contract;

public class RepoConfig
{
    public string Id { get; set; } = string.Empty; // e.g., "core"
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public bool Enabled { get; set; } = false;
    public string GpgKey { get; set; } = string.Empty;
}

public class AuroraRepoFile
{
    public RepoMetadata Metadata { get; set; } = new();
    public List<Models.Package> Packages { get; set; } = new();
}

public class RepoMetadata
{
    public string Creator { get; set; } = string.Empty;
    public long Timestamp { get; set; }
    public string Description { get; set; } = string.Empty;
}