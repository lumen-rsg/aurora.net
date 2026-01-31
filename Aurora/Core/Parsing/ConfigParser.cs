namespace Aurora.Core.Parsing;

public static class ConfigParser
{
    public static Dictionary<string, string> ParseRepoConfig(string content)
    {
        var repos = new Dictionary<string, string>();
        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            
            // Skip comments and empty lines
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#"))
                continue;

            // Expecting: name = url
            var parts = trimmed.Split('=', 2);
            if (parts.Length == 2)
            {
                var name = parts[0].Trim();
                var url = parts[1].Trim();
                repos[name] = url;
            }
        }

        return repos;
    }
}