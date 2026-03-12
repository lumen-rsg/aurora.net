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
            
            // The primary package we need to satisfy is always the first token
            // e.g., "glibc-gconv-extra(x86-64) = 2.42 if redhat-rpm-config" -> "glibc-gconv-extra(x86-64)"
            Name = tokens[0];

            // Check if the next token is a standard version operator
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
        
        string versionToCompare = pkg.Version;

        // Extract version from virtual provides if present
        if (provParts.Length >= 3 && IsVersionOperator(provParts[1]))
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
            "==" => cmp == 0,
            ">"  => cmp > 0,
            "<"  => cmp < 0,
            _    => true
        };
    }
}