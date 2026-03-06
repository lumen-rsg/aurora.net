using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Aurora.Core.Models;
using Aurora.Core.State;

namespace Aurora.Core.Logic;

public class SystemUpdater
{
    public record UpdatePair(string OldVer, string NewVer, Package NewPkg);

    /// <summary>
    /// Calculates which packages need to be updated by comparing the RPM local DB with the repository data.
    /// </summary>
    public static List<UpdatePair> CalculateUpdates(IEnumerable<Package> repoPackages, string sysRoot = "/")
    {
        var plan = new List<UpdatePair>();
        
        // 1. Get currently installed packages directly from the RPM DB
        var installed = RpmLocalDb.GetInstalledPackages(sysRoot);
        
        // 2. Create a fast lookup for repository packages.
        // If a repo has multiple versions of the same package, keep the newest one.
        var repoDict = new Dictionary<string, Package>();
        foreach (var pkg in repoPackages)
        {
            if (!repoDict.TryGetValue(pkg.Name, out var existing) || 
                VersionComparer.IsNewer(existing.FullVersion, pkg.FullVersion))
            {
                repoDict[pkg.Name] = pkg;
            }
        }

        // 3. Compare installed vs remote
        foreach (var local in installed)
        {
            if (repoDict.TryGetValue(local.Name, out var remote))
            {
                // FullVersion formats as Epoch:Version-Release (e.g., 1:1.2.3-4)
                // Our VersionComparer handles this format perfectly.
                if (VersionComparer.IsNewer(local.FullVersion, remote.FullVersion))
                {
                    plan.Add(new UpdatePair(local.FullVersion, remote.FullVersion, remote));
                }
            }
        }

        return plan;
    }

    /// <summary>
    /// Applies the downloaded RPM updates via the native RPM binary.
    /// </summary>
    public static void ApplyUpdates(IEnumerable<string> rpmFilePaths, string sysRoot = "/", bool force = false, Action<string>? logAction = null)
    {
        var paths = rpmFilePaths.ToList();
        if (paths.Count == 0) return;

        logAction?.Invoke("Handing over transaction to RPM...");

        // -U: Upgrade (Installs if not present, upgrades if present, removes old version)
        // -v: Verbose
        // -h: Print hash marks (progress)
        var args = new List<string> { "-Uvh" };
        
        if (sysRoot != "/")
        {
            args.Add("--root");
            args.Add(sysRoot);
        }
        
        if (force)
        {
            args.Add("--force");
        }
        
        // Wrap paths in quotes to prevent shell injection/spacing issues
        args.AddRange(paths.Select(p => $"\"{p}\""));

        var psi = new ProcessStartInfo
        {
            FileName = "rpm",
            Arguments = string.Join(" ", args),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) throw new Exception("Failed to start RPM process.");

        // Stream output live to the CLI
        process.OutputDataReceived += (s, e) => 
        { 
            if (!string.IsNullOrWhiteSpace(e.Data)) logAction?.Invoke(e.Data); 
        };
        
        process.ErrorDataReceived += (s, e) => 
        { 
            if (!string.IsNullOrWhiteSpace(e.Data)) logAction?.Invoke($"[yellow]{e.Data}[/]"); 
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new Exception($"RPM transaction failed with exit code {process.ExitCode}. System state is protected by RPM rollback.");
        }
    }
}