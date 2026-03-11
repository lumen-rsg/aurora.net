using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Aurora.Core.Contract;

namespace Aurora.Core.Parsing;

public static class RepoConfigParser
{
    // Define our current target release version
    private const string ReleaseVer = "43";

    public static Dictionary<string, RepoConfig> Parse(string content)
    {
        var repos = new Dictionary<string, RepoConfig>();
        RepoConfig? currentRepo = null;
        
        string baseArch = GetBaseArch();

        foreach (var rawLine in content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None))
        {
            var trimmed = rawLine.Trim();
            
            // Skip comments and empty lines
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

            // Handle key=value pairs
            if (currentRepo != null)
            {
                var parts = trimmed.Split('=', 2);
                if (parts.Length == 2)
                {
                    var key = parts[0].Trim().ToLowerInvariant();
                    var value = parts[1].Trim().Trim('"', '\'');

                    // --- RPM MACRO EXPANSION ---
                    value = value.Replace("$releasever", ReleaseVer)
                                 .Replace("$basearch", baseArch);

                    switch (key)
                    {
                        case "name": currentRepo.Name = value; break;
                        case "baseurl": currentRepo.BaseUrl = value; break;
                        case "url": currentRepo.BaseUrl = value; break;
                        case "enabled": currentRepo.Enabled = IsTruthy(value); break;
                        case "gpgcheck": currentRepo.GpgCheck = IsTruthy(value); break;
                        case "gpgkey": currentRepo.GpgKey = value; break;
                    }
                }
            }
        }
        return repos;
    }

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
                foreach (var kvp in fileRepos) allRepos[kvp.Key] = kvp.Value;
            }
            catch { /* Skip unreadable */ }
        }
        return allRepos;
    }

    private static bool IsTruthy(string value)
    {
        return value == "1" || 
               value.Equals("true", StringComparison.OrdinalIgnoreCase) || 
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    // Map .NET Architectures to RPM standard basearch strings
    private static string GetBaseArch()
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "aarch64",
            Architecture.X64 => "x86_64",
            Architecture.X86 => "i686",
            Architecture.Arm => "armhfp",
            _ => "x86_64"
        };
    }
}