using Aurora.Core.Logic;

namespace Aurora.Core.Models;

public class DependencyRequest
{
    public string Name { get; }
    public string? Version { get; }
    public string? Operator { get; } // ">=", "<=", "=", ">", "<"

    public DependencyRequest(string raw)
    {
        // Clean quotes and spaces
        raw = raw.Trim().Trim('\'').Trim('"');

        // Check for operators (order matters: >= before >)
        string[] operators = { ">=", "<=", "=", ">", "<" };
        foreach (var op in operators)
        {
            if (raw.Contains(op))
            {
                var parts = raw.Split(op, 2);
                Name = parts[0].Trim();
                Operator = op;
                Version = parts[1].Trim();
                return;
            }
        }

        // No operator found, just a name
        Name = raw;
        Operator = null;
        Version = null;
    }

    public bool IsSatisfiedBy(Package pkg)
    {
        // 1. Basic Name/Provision Check
        // (This function is usually called after the name match is confirmed by the solver,
        // but it doesn't hurt to be safe if reused elsewhere)
        
        if (Operator == null || Version == null) return true;

        string candidateVer = pkg.Version;
        string requiredVer = Version;

        // --- FIX: Implicit Pkgrel Matching ---
        // If the requirement (e.g. "26.01.0") does NOT specify a release (no '-'),
        // but the candidate ("26.01.0-1") DOES, we strip the release from the candidate.
        // This treats "26.01.0-1" as equal to "26.01.0".
        if (!requiredVer.Contains('-') && candidateVer.Contains('-'))
        {
            int hyphenIndex = candidateVer.IndexOf('-');
            if (hyphenIndex > 0)
            {
                candidateVer = candidateVer.Substring(0, hyphenIndex);
            }
        }

        int cmp = VersionComparer.Compare(candidateVer, requiredVer);

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
    
    public bool Satisfies(DependencyRequest other)
    {
        if (Name != other.Name) return false;
        if (other.Operator == null || other.Version == null) return true;
        if (Version == null) return false;

        string myVer = Version;
        string otherVer = other.Version;

        // Apply the same logic for Provides comparisons
        if (!otherVer.Contains('-') && myVer.Contains('-'))
        {
            int hyphenIndex = myVer.IndexOf('-');
            if (hyphenIndex > 0)
            {
                myVer = myVer.Substring(0, hyphenIndex);
            }
        }

        int cmp = VersionComparer.Compare(myVer, otherVer);

        return other.Operator switch
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