using Aurora.Core.IO;

namespace Aurora.CLI;

public class CliConfiguration
{
    public string SysRoot { get; }
    public string DbPath { get; }
    public bool Force { get; }
    public bool AssumeYes { get; }
    public bool SkipSig { get; }
    public bool SkipGpg { get; } // NEW
    public bool SkipDownload { get; }

    // Computed Properties for convenience
    public string RepoDir => PathHelper.GetPath(SysRoot, "var/lib/aurora");
    public string CacheDir => PathHelper.GetPath(SysRoot, "var/cache/aurora");
    public string ScriptDir => PathHelper.GetPath(SysRoot, "var/lib/aurora/scripts");
    public string RepoConfigPath => PathHelper.GetPath(SysRoot, "var/lib/aurora/repo_core.yaml");

    public CliConfiguration(string sysRoot, bool force, bool assumeYes, bool skipGpg, bool skipDownload)
    {
        SysRoot = sysRoot;
        Force = force;
        AssumeYes = assumeYes;
        SkipGpg = skipGpg;
        SkipDownload = skipDownload; // NEW
        DbPath = Aurora.Core.IO.PathHelper.GetPath(SysRoot, "var/lib/aurora/aurora.db");
    }
}