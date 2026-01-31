using System.Text.Json.Serialization;

namespace Aurora.Core.Models;

public class Package
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Arch { get; set; } = string.Empty;
    public string? Description { get; set; }

    // Dependencies
    public List<string> Depends { get; set; } = new();
    public List<string> MakeDepends { get; set; } = new();
    public List<string> Conflicts { get; set; } = new();
    public List<string> Replaces { get; set; } = new();
    public List<string> Provides { get; set; } = new();

    // --- Database Specific Fields ---
    
    // The list of files actually installed on disk
    public List<string> Files { get; set; } = new();
    
    // When was this installed?
    public DateTime? InstallDate { get; set; }
    
    // "explicit" (user asked for it) or "dependency" (pulled in automatically)
    public string InstallReason { get; set; } = "explicit"; 
    
    // NEW: Track health status
    public bool IsBroken { get; set; } = false;
    
    public string? Checksum { get; set; } 

    [JsonIgnore] 
    public string FullId => $"{Name}-{Version}-{Arch}";
    
    public override string ToString() => FullId;
}