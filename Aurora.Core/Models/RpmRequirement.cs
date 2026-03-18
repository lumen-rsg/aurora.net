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
        }
        else
        {
            Name = span.Slice(0, firstSpace).ToString();
            var rest = span.Slice(firstSpace + 1).TrimStart();
            
            int secondSpace = rest.IndexOf(' ');
            if (secondSpace != -1)
            {
                var opSpan = rest.Slice(0, secondSpace);
                if (IsVersionOperator(opSpan))
                {
                    var afterOp = rest.Slice(secondSpace + 1).TrimStart();
                    if (!afterOp.IsEmpty)
                    {
                        // Supports fallback behavior for things like: "pkgA >= 1.0 or pkgB"
                        int thirdSpace = afterOp.IndexOf(' ');
                        var versionSpan = thirdSpace == -1 ? afterOp : afterOp.Slice(0, thirdSpace);
                        
                        Operator = opSpan.ToString();
                        Version = versionSpan.ToString();
                    }
                }
            }
        }
    }

    private static bool IsVersionOperator(ReadOnlySpan<char> op)
    {
        return op is ">=" or "<=" or "=" or ">" or "<" or "==" or "!=";
    }

    public bool IsSatisfiedBy(Package pkg, string providedString)
    {
        if (Operator == null) return true;

        string versionToCompare = pkg.FullVersion; 

        // Quickly extract inline version overrides without generating Arrays via .Split()
        var provSpan = providedString.AsSpan().Trim();
        int firstSpace = provSpan.IndexOf(' ');
        if (firstSpace != -1)
        {
            var rest = provSpan.Slice(firstSpace + 1).TrimStart();
            int secondSpace = rest.IndexOf(' ');
            if (secondSpace != -1)
            {
                var opSpan = rest.Slice(0, secondSpace);
                if (IsVersionOperator(opSpan))
                {
                    var afterOp = rest.Slice(secondSpace + 1).TrimStart();
                    if (!afterOp.IsEmpty)
                    {
                        int thirdSpace = afterOp.IndexOf(' ');
                        var versionSpan = thirdSpace == -1 ? afterOp : afterOp.Slice(0, thirdSpace);
                        versionToCompare = versionSpan.ToString();
                    }
                }
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