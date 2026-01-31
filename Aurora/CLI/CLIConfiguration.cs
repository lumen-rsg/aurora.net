using Aurora.Core.IO;

namespace Aurora.CLI;

public class CliConfiguration
{
    public string SysRoot { get; }
    public string DbPath { get; }
    public bool Force { get; }
    public bool AssumeYes { get; }
    public bool SkipSig { get; }

    // Computed Properties for convenience
    public string RepoDir => PathHelper.GetPath(SysRoot, "var/lib/aurora");
    public string CacheDir => PathHelper.GetPath(SysRoot, "var/cache/aurora");
    public string ScriptDir => PathHelper.GetPath(SysRoot, "var/lib/aurora/scripts");
    public string RepoConfigPath => PathHelper.GetPath(SysRoot, "var/lib/aurora/repo_core.yaml");

    public CliConfiguration(string sysRoot, bool force, bool assumeYes)
    {
        SysRoot = sysRoot;
        Force = force;
        AssumeYes = assumeYes;
        
        // Calculate DB path relative to root
        DbPath = PathHelper.GetPath(SysRoot, "var/lib/aurora/aurora.db");
    }
}