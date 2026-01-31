using Aurora.Core.State;
using Aurora.Core.Models;
using Aurora.Core.IO;
using Aurora.Core.Logging;

namespace Aurora.Core.Logic;

public class SystemUpdater
{
    private readonly Transaction _tx;
    private readonly List<Package> _repoPackages;

    public SystemUpdater(Transaction tx, List<Package> repoPackages)
    {
        _tx = tx;
        _repoPackages = repoPackages;
    }

    public void PerformUpdate(string rootPath, Action<string> statusCallback)
    {
        statusCallback("Calculating update plan...");
        var updates = CalculateUpdates();

        if (updates.Count == 0)
        {
            statusCallback("System is already up to date.");
            return;
        }

        statusCallback($"Found {updates.Count} packages to update.");

        var pendingSwaps = new List<string>(); 

        foreach (var update in updates)
        {
            var pkgName = update.NewPkg.Name;
            var pkgFile = $"{pkgName}.au"; 
            
            // FIX: Escape brackets -> [[Staging]]
            statusCallback($"[[Staging]] {pkgName} {update.OldVer} -> {update.NewVer}");

            var manifestFiles = new List<string>();
            
            PackageInstaller.InstallPackage(pkgFile, rootPath, (physicalPath, manifestPath) => 
            {
                _tx.AppendToJournal(physicalPath); 
                pendingSwaps.Add(physicalPath);
                manifestFiles.Add(manifestPath);
            }, stagingMode: true);

            update.NewPkg.Files = manifestFiles;
        }

        statusCallback("Applying updates (Atomic Swap)...");
        
        foreach (var physicalPath in pendingSwaps)
        {
            var stagedPath = physicalPath + ".aurora_new";
            
            if (File.Exists(stagedPath))
            {
                try 
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(physicalPath)!);
                    File.Move(stagedPath, physicalPath, overwrite: true);
                }
                catch (Exception ex)
                {
                    AuLogger.Error($"Failed to swap {physicalPath}: {ex.Message}");
                    throw; 
                }
            }
        }

        foreach (var update in updates)
        {
            
            _tx.RemovePackage(update.NewPkg.Name); 
            _tx.RegisterPackage(update.NewPkg);
        }

        statusCallback("Update complete.");
    }

    private List<UpdatePair> CalculateUpdates()
    {
        var plan = new List<UpdatePair>();
        
        var installed = _tx.GetAllPackages(); 
        
        var repoDict = _repoPackages.ToDictionary(p => p.Name, p => p);

        foreach (var local in installed)
        {
            if (repoDict.TryGetValue(local.Name, out var remote))
            {
                if (VersionComparer.IsNewer(local.Version, remote.Version))
                {
                    plan.Add(new UpdatePair(local.Version, remote.Version, remote));
                }
            }
        }
        return plan;
    }

    record UpdatePair(string OldVer, string NewVer, Package NewPkg);
}