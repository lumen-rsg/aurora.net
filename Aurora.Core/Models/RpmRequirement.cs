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

        int firstSpace = span.IndexOf(' ');
        if (firstSpace == -1)
        {
            Name = span.ToString();
            return;
        }

        Name = span.Slice(0, firstSpace).ToString();
        var rest = span.Slice(firstSpace + 1).TrimStart();
        
        int opLength = 0;
        if (rest.StartsWith(">=") || rest.StartsWith("<=") || rest.StartsWith("==") || rest.StartsWith("!=")) opLength = 2;
        else if (rest.StartsWith(">") || rest.StartsWith("<") || rest.StartsWith("=")) opLength = 1;
        
        if (opLength > 0)
        {
            Operator = rest.Slice(0, opLength).ToString();
            var versionSpan = rest.Slice(opLength).TrimStart();
            
            int spaceInVersion = versionSpan.IndexOf(' ');
            if (spaceInVersion > 0)
            {
                versionSpan = versionSpan.Slice(0, spaceInVersion);
            }
            Version = versionSpan.ToString();
        }
    }

    public bool IsSatisfiedBy(Package pkg, string providedString)
    {
        if (Operator == null) return true;

        string versionToCompare = pkg.FullVersion; 
        var provSpan = providedString.AsSpan().Trim();
        
        int firstSpace = provSpan.IndexOf(' ');
        if (firstSpace != -1)
        {
            var rest = provSpan.Slice(firstSpace + 1).TrimStart();
            
            int opLength = 0;
            if (rest.StartsWith(">=") || rest.StartsWith("<=") || rest.StartsWith("==") || rest.StartsWith("!=")) opLength = 2;
            else if (rest.StartsWith(">") || rest.StartsWith("<") || rest.StartsWith("=")) opLength = 1;
            
            if (opLength > 0)
            {
                var vSpan = rest.Slice(opLength).TrimStart();
                int spaceInVersion = vSpan.IndexOf(' ');
                if (spaceInVersion > 0) vSpan = vSpan.Slice(0, spaceInVersion);
                
                versionToCompare = vSpan.ToString();
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