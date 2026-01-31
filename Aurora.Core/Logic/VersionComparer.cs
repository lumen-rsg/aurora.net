using System.Text;

namespace Aurora.Core.Logic;

public static class VersionComparer
{
    /// <summary>
    /// Returns true if 'candidateVer' is greater than 'currentVer'.
    /// Implements logic similar to rpmvercmp.
    /// </summary>
    public static bool IsNewer(string currentVer, string candidateVer)
    {
        return Compare(candidateVer, currentVer) > 0;
    }

    /// <summary>
    /// Compares two version strings.
    /// Returns 1 if v1 > v2, -1 if v1 < v2, 0 if equal.
    /// </summary>
    public static int Compare(string v1, string v2)
    {
        if (string.Equals(v1, v2, StringComparison.Ordinal)) return 0;

        int i1 = 0, i2 = 0;
        int len1 = v1.Length, len2 = v2.Length;

        while (i1 < len1 || i2 < len2)
        {
            // 1. Skip separators (., -, _, +, etc)
            while (i1 < len1 && !char.IsLetterOrDigit(v1[i1])) i1++;
            while (i2 < len2 && !char.IsLetterOrDigit(v2[i2])) i2++;

            // 2. Check for end of strings
            // If both ended at the same time, they are equal up to here
            if (i1 >= len1 && i2 >= len2) return 0;

            // If one ended, the one remaining is usually "newer" (longer)
            // e.g. 1.0 < 1.0.1
            if (i1 >= len1) return -1; // v1 ran out, v2 is longer/newer
            if (i2 >= len2) return 1;  // v2 ran out, v1 is longer/newer

            // 3. Compare Segments
            if (char.IsDigit(v1[i1]))
            {
                if (char.IsDigit(v2[i2]))
                {
                    // Both are Numbers: Numeric Comparison
                    string n1 = ExtractNumber(v1, ref i1);
                    string n2 = ExtractNumber(v2, ref i2);
                    
                    int diff = CompareNumeric(n1, n2);
                    if (diff != 0) return diff;
                }
                else
                {
                    // v1 is Number, v2 is Letter -> Number > Letter (RPM Rule)
                    // e.g. 1.0 > 1.a
                    return 1;
                }
            }
            else // v1 is Letter
            {
                if (char.IsDigit(v2[i2]))
                {
                    // v1 is Letter, v2 is Number -> Letter < Number
                    return -1;
                }
                else
                {
                    // Both are Letters: Lexical Comparison
                    string s1 = ExtractAlpha(v1, ref i1);
                    string s2 = ExtractAlpha(v2, ref i2);
                    
                    int diff = string.Compare(s1, s2, StringComparison.Ordinal);
                    if (diff != 0) return diff;
                }
            }
        }

        return 0;
    }

    private static string ExtractNumber(string s, ref int i)
    {
        int start = i;
        // Eat all digits
        while (i < s.Length && char.IsDigit(s[i])) i++;
        
        // Return segment, trim leading zeros for accurate comparison (01 == 1)
        var num = s.Substring(start, i - start).TrimStart('0');
        return string.IsNullOrEmpty(num) ? "0" : num;
    }

    private static string ExtractAlpha(string s, ref int i)
    {
        int start = i;
        // Eat all letters
        while (i < s.Length && char.IsLetter(s[i])) i++;
        return s.Substring(start, i - start);
    }

    private static int CompareNumeric(string n1, string n2)
    {
        // First compare by length (longer string of digits is bigger number)
        if (n1.Length > n2.Length) return 1;
        if (n1.Length < n2.Length) return -1;
        
        // If lengths equal, compare lexically (which works for numbers of same length)
        return string.Compare(n1, n2, StringComparison.Ordinal);
    }
}