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
        (string major, string minor) = GetReleaseVerParts();

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

                    // Macro expansion
                    value = value.Replace("$releasever_major", major)
                                 .Replace("$releasever_minor", minor);

                    // $releasever must come after $releasever_major/$releasever_minor
                    // to avoid double-replacement (e.g., "42" in "$releasever_major")
                    value = value.Replace("$releasever", major + (string.IsNullOrEmpty(minor) ? "" : "." + minor))
                                 .Replace("$basearch", baseArch);

                    switch (key)
                    {
                        case "name": currentRepo.Name = value; break;
                        case "baseurl": currentRepo.BaseUrl = value; break;
                        case "metalink": currentRepo.Metalink = value; break;
                        case "mirrorlist": currentRepo.Mirrorlist = value; break;
                        case "enabled": currentRepo.Enabled = (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)); break;
                        case "gpgcheck": currentRepo.GpgCheck = (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)); break;
                        case "gpgkey": currentRepo.GpgKey = value; break;
                        case "priority":
                            if (int.TryParse(value, out var prio)) currentRepo.Priority = prio;
                            break;
                        case "cost":
                            if (int.TryParse(value, out var cost)) currentRepo.Cost = cost;
                            break;
                        case "exclude": currentRepo.Exclude = value; break;
                        case "includepkgs": currentRepo.IncludePkgs = value; break;
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

    public static string Serialize(RepoConfig repo)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[{repo.Id}]");
        sb.AppendLine($"name={repo.Name}");
        if (!string.IsNullOrEmpty(repo.BaseUrl))
            sb.AppendLine($"baseurl={repo.BaseUrl}");
        if (!string.IsNullOrEmpty(repo.Metalink))
            sb.AppendLine($"metalink={repo.Metalink}");
        if (!string.IsNullOrEmpty(repo.Mirrorlist))
            sb.AppendLine($"mirrorlist={repo.Mirrorlist}");
        sb.AppendLine($"enabled={Convert.ToInt32(repo.Enabled)}");
        sb.AppendLine($"gpgcheck={Convert.ToInt32(repo.GpgCheck)}");
        if (!string.IsNullOrEmpty(repo.GpgKey))
            sb.AppendLine($"gpgkey={repo.GpgKey}");
        if (repo.Priority != 99)
            sb.AppendLine($"priority={repo.Priority}");
        if (repo.Cost != 1000)
            sb.AppendLine($"cost={repo.Cost}");
        if (!string.IsNullOrEmpty(repo.Exclude))
            sb.AppendLine($"exclude={repo.Exclude}");
        if (!string.IsNullOrEmpty(repo.IncludePkgs))
            sb.AppendLine($"includepkgs={repo.IncludePkgs}");
        return sb.ToString();
    }

    public static void SaveAllRepos(Dictionary<string, RepoConfig> repos)
    {
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

        foreach (var kvp in grouped)
        {
            var content = new System.Text.StringBuilder();
            foreach (var repo in kvp.Value)
            {
                content.AppendLine(Serialize(repo));
            }
            File.WriteAllText(kvp.Key, content.ToString());
        }

        if (orphans.Count > 0)
        {
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

    public static void RemoveRepo(Dictionary<string, RepoConfig> allRepos, string repoId)
    {
        if (!allRepos.TryGetValue(repoId, out var repo)) return;

        allRepos.Remove(repoId);

        if (string.IsNullOrEmpty(repo.SourceFile) || !File.Exists(repo.SourceFile))
            return;

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

    private static (string major, string minor) GetReleaseVerParts()
    {
        string version = GetRawReleaseVer();
        var parts = version.Split('.', 2);
        return (parts[0], parts.Length > 1 ? parts[1] : "");
    }

    private static string GetRawReleaseVer()
    {
        var envVar = Environment.GetEnvironmentVariable("AURORA_RELEASEVER");
        if (!string.IsNullOrEmpty(envVar)) return envVar;

        try {
            if (File.Exists("/etc/os-release")) {
                var lines = File.ReadAllLines("/etc/os-release");
                foreach (var line in lines) {
                    if (line.StartsWith("VERSION_ID="))
                        return line.Split('=')[1].Trim('"');
                }
            }
        } catch { }

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
