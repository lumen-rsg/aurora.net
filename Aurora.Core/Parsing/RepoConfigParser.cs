using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.IO;
using Aurora.Core.Contract;

namespace Aurora.Core.Parsing;

public static class RepoConfigParser
{
    public static Dictionary<string, RepoConfig> Parse(string content)
    {
        var repos = new Dictionary<string, RepoConfig>();
        RepoConfig? currentRepo = null;
        
        string baseArch = GetBaseArch();
        string releaseVer = GetReleaseVer();

        foreach (var rawLine in content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None))
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#') || trimmed.StartsWith(';')) 
                continue;

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                var id = trimmed.Substring(1, trimmed.Length - 2).Trim();
                currentRepo = new RepoConfig { Id = id };
                repos[id] = currentRepo;
                continue;
            }

            if (currentRepo != null)
            {
                var parts = trimmed.Split('=', 2);
                if (parts.Length == 2)
                {
                    var key = parts[0].Trim().ToLowerInvariant();
                    var value = parts[1].Trim().Trim('"', '\'');

                    // --- MACRO EXPANSION ---
                    value = value.Replace("$releasever", releaseVer)
                                 .Replace("$basearch", baseArch);

                    switch (key)
                    {
                        case "name": currentRepo.Name = value; break;
                        case "baseurl": currentRepo.BaseUrl = value; break;
                        case "enabled": currentRepo.Enabled = (value == "1" || value.ToLower() == "true"); break;
                        case "gpgcheck": currentRepo.GpgCheck = (value == "1" || value.ToLower() == "true"); break;
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
        if (!Directory.Exists(directoryPath)) return allRepos;
        foreach (var file in Directory.GetFiles(directoryPath, "*.repo"))
        {
            try {
                var fileRepos = Parse(File.ReadAllText(file));
                foreach (var kvp in fileRepos) allRepos[kvp.Key] = kvp.Value;
            } catch { }
        }
        return allRepos;
    }

    private static string GetReleaseVer()
    {
        // 1. Priority: Environment Variable (Useful for bootstrapping)
        var envVar = Environment.GetEnvironmentVariable("AURORA_RELEASEVER");
        if (!string.IsNullOrEmpty(envVar)) return envVar;

        // 2. Try to read from host /etc/os-release
        try {
            if (File.Exists("/etc/os-release")) {
                var lines = File.ReadAllLines("/etc/os-release");
                foreach (var line in lines) {
                    if (line.StartsWith("VERSION_ID="))
                        return line.Split('=')[1].Trim('"');
                }
            }
        } catch { }

        // 3. Fallback to your current server's version
        return "26.03"; 
    }

    private static string GetBaseArch()
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "aarch64",
            Architecture.X64 => "x86_64",
            _ => "x86_64"
        };
    }
}