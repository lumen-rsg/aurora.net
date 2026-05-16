using System.Collections.Generic;
using System.Linq;

namespace Aurora.Core.Parsing;

public static class MirrorlistParser
{
    public static List<string> ParseMirrorlist(string content)
    {
        return content
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrEmpty(line) && !line.StartsWith('#'))
            .ToList();
    }
}
