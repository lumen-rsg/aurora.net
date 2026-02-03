namespace Aurora.Core.Models;

public class Package
{
    // Core Identity
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Arch { get; set; } = string.Empty;
    public string? Description { get; set; }

    // Metadata
    public string? Maintainer { get; set; } // NEW
    public string? Url { get; set; }        // NEW
    public List<string> Licenses { get; set; } = new(); // NEW
    public long BuildDate { get; set; } // NEW

    // Dependency Graph
    public List<string> Depends { get; set; } = new(); // Maps from dependencies.runtime
    public List<string> MakeDepends { get; set; } = new();
    public List<string> OptDepends { get; set; } = new(); // Maps from dependencies.optional
    public List<string> Conflicts { get; set; } = new();
    public List<string> Replaces { get; set; } = new();
    public List<string> Provides { get; set; } = new();
    public List<string> Backup { get; set; } = new(); // NEW: Config files to backup
    public string FileName { get; set; } = string.Empty;

    // Security
    public string? Checksum { get; set; }
    public long InstalledSize { get; set; } // Maps from files.package_size

    // Internal State (Database specific, not in YAML)
    public List<string> Files { get; set; } = new();
    public string InstallReason { get; set; } = "explicit";
    public bool IsBroken { get; set; } = false;

    public string FullId => $"{Name}-{Version}-{Arch}";
    public override string ToString() => FullId;
}