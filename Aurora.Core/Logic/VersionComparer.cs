using System;
using System.Collections.Generic;

namespace Aurora.Core.Logic;

public class VersionComparer : IComparer<string>
{
    /// <summary>
    /// Explicit interface implementation for LINQ OrderBy/OrderByDescending.
    /// </summary>
    int IComparer<string>.Compare(string? x, string? y)
    {
        return Compare(x ?? "0", y ?? "0");
    }

    /// <summary>
    /// Returns true if 'candidateVer' is strictly newer than 'currentVer'.
    /// </summary>
    public static bool IsNewer(string currentVer, string candidateVer)
    {
        return Compare(candidateVer, currentVer) > 0;
    }

    /// <summary>
    /// Compares two RPM version strings (E:V-R).
    /// Returns 1 if v1 > v2, -1 if v1 < v2, 0 if equal.
    /// </summary>
    public static int Compare(string v1, string v2)
    {
        if (string.Equals(v1, v2, StringComparison.Ordinal)) return 0;

        // 1. Parse into Epoch, Version, and Release
        var (e1, ver1, r1) = ParseFullVersion(v1);
        var (e2, ver2, r2) = ParseFullVersion(v2);

        // 2. Compare Epochs (Numeric comparison)
        int cmp = CompareNumericStrings(e1, e2);
        if (cmp != 0) return cmp;

        // 3. Compare Version segments
        cmp = CompareSegments(ver1, ver2);
        if (cmp != 0) return cmp;

        // 4. Compare Release segments 
        // Note: If one side lacks a release (common in requirements), we consider them equal at this stage
        if (string.IsNullOrEmpty(r1) || string.IsNullOrEmpty(r2)) return 0;
        
        return CompareSegments(r1, r2);
    }

    private static (string Epoch, string Version, string Release) ParseFullVersion(string v)
    {
        string epoch = "0";
        string version = v;
        string release = "";

        // Look for Epoch (e.g., "1:5.3.0")
        int colonIdx = v.IndexOf(':');
        if (colonIdx != -1)
        {
            epoch = v.Substring(0, colonIdx);
            version = v.Substring(colonIdx + 1);
        }

        // Look for Release (e.g., "5.3.0-2.fc43")
        int dashIdx = version.LastIndexOf('-');
        if (dashIdx != -1)
        {
            release = version.Substring(dashIdx + 1);
            version = version.Substring(0, dashIdx);
        }

        return (epoch, version, release);
    }

    private static int CompareSegments(string s1, string s2)
    {
        int i1 = 0, i2 = 0;
        while (i1 < s1.Length || i2 < s2.Length)
        {
            // Skip non-alphanumeric characters (separators)
            while (i1 < s1.Length && !char.IsLetterOrDigit(s1[i1])) i1++;
            while (i2 < s2.Length && !char.IsLetterOrDigit(s2[i2])) i2++;

            if (i1 >= s1.Length && i2 >= s2.Length) return 0;
            if (i1 >= s1.Length) return -1;
            if (i2 >= s2.Length) return 1;

            string seg1, seg2;
            bool isDigit1 = char.IsDigit(s1[i1]);
            
            if (isDigit1)
            {
                seg1 = ExtractNumber(s1, ref i1);
                // Numbers are always newer than letters
                if (!char.IsDigit(s2[i2])) return 1; 
                
                seg2 = ExtractNumber(s2, ref i2);
                int diff = CompareNumericStrings(seg1, seg2);
                if (diff != 0) return diff;
            }
            else
            {
                seg1 = ExtractAlpha(s1, ref i1);
                // Letters are always older than numbers
                if (char.IsDigit(s2[i2])) return -1;
                
                seg2 = ExtractAlpha(s2, ref i2);
                int diff = string.Compare(seg1, seg2, StringComparison.Ordinal);
                if (diff != 0) return diff;
            }
        }
        return 0;
    }

    private static string ExtractNumber(string s, ref int i)
    {
        int start = i;
        while (i < s.Length && char.IsDigit(s[i])) i++;
        var num = s.Substring(start, i - start).TrimStart('0');
        return num.Length == 0 ? "0" : num;
    }

    private static string ExtractAlpha(string s, ref int i)
    {
        int start = i;
        while (i < s.Length && char.IsLetter(s[i])) i++;
        return s.Substring(start, i - start);
    }

    private static int CompareNumericStrings(string n1, string n2)
    {
        // Numeric comparison by magnitude
        if (n1.Length > n2.Length) return 1;
        if (n1.Length < n2.Length) return -1;
        return string.Compare(n1, n2, StringComparison.Ordinal);
    }
}