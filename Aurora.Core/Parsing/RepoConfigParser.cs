using System;
using System.Collections.Generic;
using Aurora.Core.Contract;

namespace Aurora.Core.Parsing;

public static class RepoConfigParser
{
    /// <summary>
    /// Parses the content of a standard RPM .repo file.
    /// </summary>
    public static Dictionary<string, RepoConfig> Parse(string content)
    {
        var repos = new Dictionary<string, RepoConfig>();
        RepoConfig? currentRepo = null;

        foreach (var rawLine in content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None))
        {
            var trimmed = rawLine.Trim();
            
            // Skip comments (RPM allows # and ;) and empty lines
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#') || trimmed.StartsWith(';')) 
                continue;

            // Check for new repository section: [repo_id]
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                var id = trimmed.Substring(1, trimmed.Length - 2).Trim();
                currentRepo = new RepoConfig { Id = id };
                repos[id] = currentRepo;
                continue;
            }

            // Handle key=value pairs for the current section
            if (currentRepo != null)
            {
                var parts = trimmed.Split('=', 2);
                if (parts.Length == 2)
                {
                    var key = parts[0].Trim().ToLowerInvariant();
                    var value = parts[1].Trim();

                    // Strip quotes if any (sometimes values in .repo files are wrapped)
                    value = value.Trim('"', '\'');

                    switch (key)
                    {
                        case "name": 
                            currentRepo.Name = value; 
                            break;
                        case "baseurl": 
                            currentRepo.BaseUrl = value; 
                            break;
                        case "url": // Legacy alias just in case
                            currentRepo.BaseUrl = value; 
                            break;
                        case "enabled": 
                            currentRepo.Enabled = IsTruthy(value); 
                            break;
                        case "gpgcheck": 
                            currentRepo.GpgCheck = IsTruthy(value); 
                            break;
                        case "gpgkey": 
                            currentRepo.GpgKey = value; 
                            break;
                    }
                }
            }
        }
        return repos;
    }

    /// <summary>
    /// Parses an entire directory of .repo files (e.g., /etc/yum.repos.d/)
    /// </summary>
    public static Dictionary<string, RepoConfig> ParseDirectory(string directoryPath)
    {
        var allRepos = new Dictionary<string, RepoConfig>();

        if (!System.IO.Directory.Exists(directoryPath)) 
            return allRepos;

        foreach (var file in System.IO.Directory.GetFiles(directoryPath, "*.repo"))
        {
            try
            {
                var content = System.IO.File.ReadAllText(file);
                var fileRepos = Parse(content);
                
                foreach (var kvp in fileRepos)
                {
                    // If multiple files define the same repo ID, the last one parsed wins
                    allRepos[kvp.Key] = kvp.Value;
                }
            }
            catch
            {
                // Skip unreadable files
            }
        }

        return allRepos;
    }

    // Helper to evaluate RPM booleans (1/0 or true/false)
    private static bool IsTruthy(string value)
    {
        return value == "1" || 
               value.Equals("true", StringComparison.OrdinalIgnoreCase) || 
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}