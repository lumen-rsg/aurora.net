using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Aurora.Core.Logging;
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
    public static void ApplyUpdates(IEnumerable<string> rpmFilePaths, string sysRoot = "/", bool force = false, bool skipGpg = false, Action<string>? logAction = null)
    {
        // Filter out any potential nulls from the array
        var paths = rpmFilePaths.Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (paths.Count == 0) return;

        logAction?.Invoke("Handing over transaction to RPM...");

        // --- CRITICAL FIX: Prevent Host Seed Contamination ---
        // If the sysroot was seeded with a host OS (like Ubuntu) to provide an initial /bin/sh,
        // it contains a stale ld.so.cache. This forces the new dynamically linked binaries 
        // to incorrectly load the host's older libc.so.6. We must drop it.
        if (sysRoot != "/")
        {
            var ldCachePath = Path.Combine(sysRoot, "etc", "ld.so.cache");
            if (File.Exists(ldCachePath))
            {
                try { File.Delete(ldCachePath); } catch { /* Ignore locked files */ }
            }
        }

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

        if (skipGpg)
        {
            args.Add("--nosignature");
            args.Add("--nodigest");
        }
        
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

        // --- CRITICAL FIX: Environment Isolation ---
        // Prevent host paths from poisoning the chroot's script execution environment
        psi.Environment.Remove("LD_LIBRARY_PATH");
        psi.Environment.Remove("LD_PRELOAD");

        using var process = Process.Start(psi);
        if (process == null) throw new Exception("Failed to start RPM process.");

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
            AuLogger.Error($"SystemUpdater: RPM transaction failed with exit code {process.ExitCode}.");
            throw new Exception($"RPM transaction failed with exit code {process.ExitCode}. System state is protected by RPM rollback.");
        }
        
        AuLogger.Info($"SystemUpdater: RPM transaction completed successfully ({paths.Count} packages).");
    }
    
}