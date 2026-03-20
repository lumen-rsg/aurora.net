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
        
        // Reliably locate the operator even if spaces are completely missing
        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];
            if (c == '>' || c == '<' || c == '=' || c == '!')
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
        if (Operator == null) return true;

        string versionToCompare = pkg.FullVersion; 
        var provSpan = providedString.AsSpan().Trim();
        
        int opIdx = -1;
        int opLen = 0;
        for (int i = 0; i < provSpan.Length; i++)
        {
            char c = provSpan[i];
            if (c == '>' || c == '<' || c == '=' || c == '!')
            {
                opIdx = i;
                if (i + 1 < provSpan.Length && provSpan[i + 1] == '=') opLen = 2;
                else opLen = 1;
                break;
            }
        }

        if (opIdx != -1)
        {
            var vSpan = provSpan.Slice(opIdx + opLen).TrimStart();
            int spaceInVersion = vSpan.IndexOf(' ');
            if (spaceInVersion > 0) vSpan = vSpan.Slice(0, spaceInVersion);
            versionToCompare = vSpan.ToString();
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