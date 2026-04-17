namespace Aurora.Core.Models;

public class Package
{
    public string Name { get; set; } = string.Empty;
    public string Epoch { get; set; } = "0";
    public string Version { get; set; } = string.Empty;
    public string Release { get; set; } = string.Empty;
    public string Arch { get; set; } = string.Empty;
    
    public string Summary { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string License { get; set; } = string.Empty;
    
    // Physical file location in the repo (e.g., "Packages/z/zlib-1.2.11-3.fc38.aarch64.rpm")
    public string LocationHref { get; set; } = string.Empty;
    
    // NEW: Track which repository this package belongs to
    public string RepositoryId { get; set; } = string.Empty; 
    public string Checksum { get; set; } = string.Empty;
    public long Size { get; set; }
    public long InstalledSize { get; set; }

    // Dependency Graph (Capabilities)
    public List<string> Requires { get; set; } = new();
    public List<string> Recommends { get; set; } = new();
    public List<string> Provides { get; set; } = new();
    public List<string> Conflicts { get; set; } = new();
    public List<string> Obsoletes { get; set; } = new();

    // Helper for formatting
    public string FullVersion => Epoch == "0" ? $"{Version}-{Release}" : $"{Epoch}:{Version}-{Release}";
    public string Nevra => $"{Name}-{FullVersion}.{Arch}";

    public override string ToString() => Nevra;
}