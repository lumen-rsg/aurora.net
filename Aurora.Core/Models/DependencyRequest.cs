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
        if (Operator == null || Version == null) return true;

        int cmp = VersionComparer.Compare(pkg.Version, Version);

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
    
    // Add this method to DependencyRequest class
    public bool Satisfies(DependencyRequest other)
    {
        // If names don't match, they don't satisfy
        if (Name != other.Name) return false;

        // If the other (the dependency) doesn't care about version, we are good
        if (other.Operator == null || other.Version == null) return true;

        // If we (the provision) don't have a version, but they require one, we usually fail
        if (Version == null) return false;

        // Compare our version against their constraint
        int cmp = VersionComparer.Compare(Version, other.Version);

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