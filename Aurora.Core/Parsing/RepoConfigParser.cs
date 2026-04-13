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
                foreach (var kvp in fileRepos)
                {
                    kvp.Value.SourceFile = file;
                    allRepos[kvp.Key] = kvp.Value;
                }
            } catch { }
        }
        return allRepos;
    }

    /// <summary>
    /// Serializes a single RepoConfig to INI-style .repo format text.
    /// </summary>
    public static string Serialize(RepoConfig repo)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[{repo.Id}]");
        sb.AppendLine($"name={repo.Name}");
        if (!string.IsNullOrEmpty(repo.BaseUrl))
            sb.AppendLine($"baseurl={repo.BaseUrl}");
        sb.AppendLine($"enabled={Convert.ToInt32(repo.Enabled)}");
        sb.AppendLine($"gpgcheck={Convert.ToInt32(repo.GpgCheck)}");
        if (!string.IsNullOrEmpty(repo.GpgKey))
            sb.AppendLine($"gpgkey={repo.GpgKey}");
        return sb.ToString();
    }

    /// <summary>
    /// Writes a collection of repos back to their respective .repo files.
    /// Repos that share the same SourceFile are written together.
    /// New repos (no SourceFile) are written to a new file.
    /// </summary>
    public static void SaveAllRepos(Dictionary<string, RepoConfig> repos)
    {
        // Group by source file
        var grouped = new Dictionary<string, List<RepoConfig>>();
        var orphans = new List<RepoConfig>();

        foreach (var repo in repos.Values)
        {
            if (!string.IsNullOrEmpty(repo.SourceFile) && File.Exists(repo.SourceFile))
            {
                if (!grouped.ContainsKey(repo.SourceFile))
                    grouped[repo.SourceFile] = new List<RepoConfig>();
                grouped[repo.SourceFile].Add(repo);
            }
            else
            {
                orphans.Add(repo);
            }
        }

        // Write each group back to its file
        foreach (var kvp in grouped)
        {
            var content = new System.Text.StringBuilder();
            foreach (var repo in kvp.Value)
            {
                content.AppendLine(Serialize(repo));
            }
            File.WriteAllText(kvp.Key, content.ToString());
        }

        // Write orphans to a new file
        if (orphans.Count > 0)
        {
            // Determine directory from existing repos, or fallback
            string? dir = repos.Values
                .FirstOrDefault(r => !string.IsNullOrEmpty(r.SourceFile))?.SourceFile;
            
            if (dir != null)
                dir = Path.GetDirectoryName(dir);
            else
                dir = "/etc/yum.repos.d";

            var newFile = Path.Combine(dir!, "aurora-custom.repo");
            var content = new System.Text.StringBuilder();
            foreach (var repo in orphans)
            {
                repo.SourceFile = newFile;
                content.AppendLine(Serialize(repo));
            }
            File.WriteAllText(newFile, content.ToString());
        }
    }

    /// <summary>
    /// Removes a repo by rewriting its source file without it.
    /// If the file becomes empty, it is deleted.
    /// </summary>
    public static void RemoveRepo(Dictionary<string, RepoConfig> allRepos, string repoId)
    {
        if (!allRepos.TryGetValue(repoId, out var repo)) return;
        
        allRepos.Remove(repoId);

        if (string.IsNullOrEmpty(repo.SourceFile) || !File.Exists(repo.SourceFile))
            return;

        // Collect remaining repos that belong to the same file
        var sameFileRepos = allRepos.Values
            .Where(r => r.SourceFile == repo.SourceFile)
            .ToList();

        if (sameFileRepos.Count == 0)
        {
            File.Delete(repo.SourceFile);
        }
        else
        {
            var content = new System.Text.StringBuilder();
            foreach (var r in sameFileRepos)
            {
                content.AppendLine(Serialize(r));
            }
            File.WriteAllText(repo.SourceFile, content.ToString());
        }
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