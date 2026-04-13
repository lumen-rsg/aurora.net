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
        var span = raw.AsSpan().Trim();

        if (span.StartsWith("("))
        {
            IsRichDependency = true;
            span = span.Slice(1, span.Length - 2).Trim();
        }
        else
        {
            IsRichDependency = false;
        }

        int opIdx = -1;
        int opLen = 0;
        int parenDepth = 0;
        
        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];
            if (c == '(') parenDepth++;
            else if (c == ')') parenDepth--;
            else if (parenDepth == 0 && (c == '>' || c == '<' || c == '=' || c == '!'))
            {
                opIdx = i;
                if (i + 1 < span.Length && span[i + 1] == '=') opLen = 2;
                else opLen = 1;
                break;
            }
        }

        if (opIdx != -1)
        {
            Name = span.Slice(0, opIdx).TrimEnd().ToString();
            Operator = span.Slice(opIdx, opLen).ToString();
            
            var vSpan = span.Slice(opIdx + opLen).TrimStart();
            int spaceInVersion = vSpan.IndexOf(' ');
            if (spaceInVersion > 0) vSpan = vSpan.Slice(0, spaceInVersion);
            
            Version = vSpan.ToString();
        }
        else
        {
            int firstSpace = span.IndexOf(' ');
            if (firstSpace == -1)
            {
                Name = span.ToString();
            }
            else
            {
                Name = span.Slice(0, firstSpace).ToString();
                var vSpan = span.Slice(firstSpace + 1).TrimStart();
                int secondSpace = vSpan.IndexOf(' ');
                if (secondSpace > 0) vSpan = vSpan.Slice(0, secondSpace);
                Version = vSpan.ToString();
            }
        }
    }

    public bool IsSatisfiedBy(Package pkg, string providedString)
    {
        // If the requirement doesn't care about the version, any provide matches
        if (Operator == null) return true;

        string? versionToCompare = null; 
        var provSpan = providedString.AsSpan().Trim();
        
        int opIdx = -1;
        int opLen = 0;
        int parenDepth = 0;
        for (int i = 0; i < provSpan.Length; i++)
        {
            char c = provSpan[i];
            if (c == '(') parenDepth++;
            else if (c == ')') parenDepth--;
            else if (parenDepth == 0 && (c == '>' || c == '<' || c == '=' || c == '!'))
            {
                opIdx = i;
                if (i + 1 < provSpan.Length && provSpan[i + 1] == '=') opLen = 2;
                else opLen = 1;
                break;
            }
        }

        string provName;
        if (opIdx != -1)
        {
            provName = provSpan.Slice(0, opIdx).TrimEnd().ToString();
            var vSpan = provSpan.Slice(opIdx + opLen).TrimStart();
            int spaceInVersion = vSpan.IndexOf(' ');
            if (spaceInVersion > 0) vSpan = vSpan.Slice(0, spaceInVersion);
            versionToCompare = vSpan.ToString();
        }
        else
        {
            int spaceIdx = providedString.IndexOf(' ');
            provName = spaceIdx > 0 ? providedString.Substring(0, spaceIdx) : providedString;
        }

        if (versionToCompare == null)
        {
            // --- CRITICAL RPM RULE FIX ---
            // Unversioned virtual provides (like "lua(abi)") CANNOT satisfy a versioned requirement.
            // A provide only inherits the package's version if it is implicitly the package's own Name.
            bool isImplicit = provName.Equals(pkg.Name, StringComparison.OrdinalIgnoreCase) ||
                              provName.StartsWith(pkg.Name + "(", StringComparison.OrdinalIgnoreCase);

            if (isImplicit)
            {
                versionToCompare = pkg.FullVersion;
            }
            else
            {
                // It's a virtual provide with no version attached. It automatically fails.
                return false; 
            }
        }

        if (Version == null) return true;

        if (!Version.Contains('-') && versionToCompare.Contains('-'))
        {
            int dashIndex = versionToCompare.LastIndexOf('-');
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