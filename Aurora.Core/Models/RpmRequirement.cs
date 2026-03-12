using System;
using Aurora.Core.Logic;

namespace Aurora.Core.Models;

public class RpmRequirement
{
    public string Name { get; }
    public string? Operator { get; }
    public string? Version { get; }
    public bool IsRichDependency { get; }

    public RpmRequirement(string raw)
    {
        raw = raw.Trim();

        // 1. Detect RPM Rich Dependencies (Boolean Logic)
        if (raw.StartsWith("("))
        {
            IsRichDependency = true;
            
            // Strip the outer parentheses
            string clean = raw.TrimStart('(').TrimEnd(')');
            
            var tokens = clean.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            
            Name = tokens[0];

            if (tokens.Length >= 3 && IsVersionOperator(tokens[1]))
            {
                Operator = tokens[1];
                Version = tokens[2];
            }
            return;
        }

        // 2. Standard Dependencies
        IsRichDependency = false;
        var parts = raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        Name = parts[0];

        if (parts.Length >= 3 && IsVersionOperator(parts[1]))
        {
            Operator = parts[1];
            Version = parts[2];
        }
    }

    private bool IsVersionOperator(string op)
    {
        return op is ">=" or "<=" or "=" or ">" or "<" or "==" or "!=";
    }

    public bool IsSatisfiedBy(Package pkg, string providedString)
    {
        if (Operator == null) return true;

        var provParts = providedString.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        
        // --- CRITICAL FIX: Use FullVersion ---
        // We must check against the Package's full Version-Release string, 
        // because RPM dependencies usually specify both.
        // If Epoch > 0, FullVersion includes "Epoch:". If the requirement doesn't have an epoch, 
        // we might need to strip it for a fair comparison, but VersionComparer usually handles it.
        string versionToCompare = pkg.FullVersion; 

        // If the provision itself explicitly states a version (e.g. virtual capability), override the package version
        if (provParts.Length >= 3 && IsVersionOperator(provParts[1]))
        {
            versionToCompare = provParts[2];
        }

        if (Version == null) return true;

        // If the requested version does NOT contain a release (no hyphen), 
        // but our FullVersion does, we should strip our release to compare apples to apples.
        // e.g. Req: "bash >= 5.0", Pkg: "5.0-1.fc39" -> Compare "5.0" to "5.0"
        if (!Version.Contains('-') && versionToCompare.Contains('-'))
        {
            // Only strip if it doesn't have an Epoch prefix containing a dash (rare, but possible)
            var dashIndex = versionToCompare.LastIndexOf('-');
            if (dashIndex > 0)
            {
                versionToCompare = versionToCompare.Substring(0, dashIndex);
            }
        }

        int cmp = VersionComparer.Compare(versionToCompare, Version);

        return Operator switch
        {
            ">=" => cmp >= 0,
            "<=" => cmp <= 0,
            "="  => cmp == 0,
            "==" => cmp == 0,
            ">"  => cmp > 0,
            "<"  => cmp < 0,
            _    => true
        };
    }
}