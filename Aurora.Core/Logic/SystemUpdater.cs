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
    /// Resolves the full dependency graph for a set of update packages,
    /// pulling in any new or upgraded dependencies required by the new versions.
    /// Returns all packages in topological (dependency-first) order and the
    /// subset that are additional dependencies not in the original update set.
    /// </summary>
    public static (List<Package> FullPlan, List<Package> AdditionalDeps) ResolveUpdateDependencies(
        List<UpdatePair> updatePairs,
        IEnumerable<Package> allRepoPackages,
        IEnumerable<Package> installedPackages,
        string sysRoot = "/",
        bool resolveRecommends = true)
    {
        var targets = updatePairs.Select(p => p.NewPkg.Name).ToList();
        var solver = new DependencySolver(allRepoPackages, installedPackages, sysRoot);
        var fullPlan = solver.Resolve(targets, resolveRecommends: resolveRecommends);

        var updateNames = new HashSet<string>(targets);
        var additionalDeps = fullPlan.Where(p => !updateNames.Contains(p.Name)).ToList();

        return (fullPlan, additionalDeps);
    }

    /// <summary>
    /// Scans plan packages for group()/user() virtual identity requirements and
    /// creates any missing system groups/users before the RPM transaction.
    /// RPM checks these at install time even though no package provides them.
    /// </summary>
    public static void PreCreateSystemIdentities(IEnumerable<Package> packages, string sysRoot = "/", Action<string>? logAction = null)
    {
        var groups = new HashSet<string>();
        var users = new HashSet<string>();

        foreach (var pkg in packages)
        {
            foreach (var req in pkg.Requires)
            {
                if (req.StartsWith("group("))
                {
                    var closeParen = req.IndexOf(')');
                    if (closeParen > 6)
                        groups.Add(req.Substring(6, closeParen - 6));
                }
                else if (req.StartsWith("user("))
                {
                    var closeParen = req.IndexOf(')');
                    if (closeParen > 5)
                        users.Add(req.Substring(5, closeParen - 5));
                }
            }
        }

        foreach (var group in groups)
        {
            if (sysRoot == "/")
                RunQuiet("groupadd", $"-f {group}", logAction);
            else
                RunQuiet("chroot", $"{sysRoot} groupadd -f {group}", logAction);
        }

        foreach (var user in users)
        {
            // system user, no login, no home
            var args = sysRoot == "/"
                ? $"-r -s /sbin/nologin {user}"
                : $"{sysRoot} useradd -r -s /sbin/nologin {user}";
            var cmd = sysRoot == "/" ? "useradd" : "chroot";
            RunQuiet(cmd, args, logAction);
        }
    }

    private static void RunQuiet(string cmd, string args, Action<string>? logAction)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = cmd,
                Arguments = args,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit();
        }
        catch (Exception ex)
        {
            logAction?.Invoke($"[grey]Note: {cmd} {args}: {ex.Message}[/]");
        }
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