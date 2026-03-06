using System;
using Aurora.Core.Logic;

namespace Aurora.Core.Models;

public class RpmRequirement
{
    public string Name { get; }
    public string? Operator { get; }
    public string? Version { get; }

    public RpmRequirement(string raw)
    {
        // Example: "systemd >= 253" or "libc.so.6()(64bit)"
        var parts = raw.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        Name = parts[0];

        if (parts.Length >= 3)
        {
            Operator = parts[1]; // >=, <=, =, >, <
            Version = parts[2];
        }
    }

    public bool IsSatisfiedBy(Package pkg, string providedString)
    {
        // 1. If it's a simple name requirement, and the name matches, we're good.
        if (Operator == null) return true;

        // 2. If the requirement has a version, we need to check the version of the provision.
        // providedString might be "bash = 5.2" or just "bash".
        var provParts = providedString.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        
        string versionToCompare = pkg.Version;

        // If the provision itself specifies a version (e.g., a virtual provide), use that.
        if (provParts.Length >= 3 && provParts[1] == "=")
        {
            versionToCompare = provParts[2];
        }

        if (Version == null) return true;

        int cmp = VersionComparer.Compare(versionToCompare, Version);

        return Operator switch
        {
            ">=" => cmp >= 0,
            "<=" => cmp <= 0,
            "="  => cmp == 0,
            ">"  => cmp > 0,
            "<"  => cmp < 0,
            _    => true
        };
    }
}