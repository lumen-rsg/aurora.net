namespace Aurora.Core.Contract;

/// <summary>
/// Root object representing the 'aurora.meta' YAML contract.
/// </summary>
public class AuroraManifest
{
    public PackageSection Package { get; set; } = new();
    public MetadataSection Metadata { get; set; } = new();
    public DependencySection Dependencies { get; set; } = new();
    public BuildSection Build { get; set; } = new();
    public FilesSection Files { get; set; } = new();
}

/// <summary>
/// Represents the 'package:' section.
/// Core identity of the artifact.
/// </summary>
public class PackageSection
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Architecture { get; set; } = string.Empty;
    public string Maintainer { get; set; } = string.Empty;
    
    // Stored as Unix Timestamp in YAML (e.g., 1769866934)
    public long BuildDate { get; set; }
    
    public string BuildTool { get; set; } = string.Empty;
    public string BuildToolVersion { get; set; } = string.Empty;
    
    public List<string> AllNames { get; set; } = new(); // NEW: Stores all names from the pkgname array
}

/// <summary>
/// Represents the 'metadata:' section.
/// Descriptive information and relationships.
/// </summary>
public class MetadataSection
{
    // 'pkgname' is often redundant with package.name but kept for contract compliance
    public string PkgName { get; set; } = string.Empty;
    
    public string Url { get; set; } = string.Empty;
    
    // Lists
    public List<string> License { get; set; } = new();
    public List<string> Groups { get; set; } = new();
    public List<string> Provides { get; set; } = new();
    public List<string> Conflicts { get; set; } = new();
    public List<string> Replaces { get; set; } = new();
    public List<string> Backup { get; set; } = new();
}

/// <summary>
/// Represents the 'dependencies:' section.
/// </summary>
public class DependencySection
{
    // Crucial for the Solver
    public List<string> Runtime { get; set; } = new();
    
    public List<string> Optional { get; set; } = new();
    
    // Build-time only (ignored during install, used during build)
    public List<string> Build { get; set; } = new();
    
    public List<string> Test { get; set; } = new();
}

/// <summary>
/// Represents the 'build:' section.
/// Information about the environment used to compile the package.
/// </summary>
public class BuildSection
{
    // e.g. "!distcc", "color"
    public List<string> Environment { get; set; } = new();
    
    // e.g. "strip", "docs"
    public List<string> Options { get; set; } = new();
    
    public string Directory { get; set; } = string.Empty;
    public string SourceDirectory { get; set; } = string.Empty;
    
    // NEW: Source files (URLs or filenames)
    public List<string> Source { get; set; } = new();
    
    // NEW: Integrity checks (we focus on sha256 for now)
    public List<string> Sha256Sums { get; set; } = new();
    
    public List<string> NoExtract { get; set; } = new(); // NEW
}

/// <summary>
/// Represents the 'files:' section.
/// Integrity and sizing.
/// </summary>
public class FilesSection
{
    public long PackageSize { get; set; }
    public string SourceHash { get; set; } = string.Empty;
}