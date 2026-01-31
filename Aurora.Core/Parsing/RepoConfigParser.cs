using Aurora.Core.Contract;

namespace Aurora.Core.Parsing;

public static class RepoConfigParser
{
    public static Dictionary<string, RepoConfig> Parse(string content)
    {
        var repos = new Dictionary<string, RepoConfig>();
        RepoConfig? currentRepo = null;

        foreach (var line in content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#')) continue;

            // Check for new section [repo_id]
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                var id = trimmed.Substring(1, trimmed.Length - 2);
                currentRepo = new RepoConfig { Id = id };
                repos[id] = currentRepo;
                continue;
            }

            // Handle key = value pairs for the current section
            if (currentRepo != null)
            {
                var parts = trimmed.Split('=', 2);
                if (parts.Length == 2)
                {
                    var key = parts[0].Trim().ToLowerInvariant();
                    var value = parts[1].Trim();

                    switch (key)
                    {
                        case "name": currentRepo.Name = value; break;
                        case "url": currentRepo.Url = value; break;
                        case "enabled": bool.TryParse(value, out var enabled); currentRepo.Enabled = enabled; break;
                        case "gpgkey": currentRepo.GpgKey = value; break;
                    }
                }
            }
        }
        return repos;
    }
}