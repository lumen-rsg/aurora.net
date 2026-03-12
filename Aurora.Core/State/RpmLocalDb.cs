using System.Diagnostics;
using Aurora.Core.Models;

namespace Aurora.Core.State;

public static class RpmLocalDb
{
    /// <summary>
    /// Gets all installed packages directly from the RPM database using a formatted query.
    /// </summary>
    public static List<Package> GetInstalledPackages(string sysRoot = "/")
    {
        var packages = new List<Package>();
        
        var psi = new ProcessStartInfo
        {
            FileName = "rpm",
            Arguments = "--root " + sysRoot + " -qa --qf \"%{NAME}|%{EPOCHNUM}|%{VERSION}|%{RELEASE}|%{ARCH}|%{SIZE}\\n\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(psi);
            if (process == null) return packages;

            string? line;
            while ((line = process.StandardOutput.ReadLine()) != null)
            {
                var parts = line.Split('|');
                if (parts.Length < 6) continue;

                // FIX: Normalize "(none)" to "0"
                string epoch = parts[1];
                if (epoch == "(none)" || string.IsNullOrWhiteSpace(epoch)) 
                {
                    epoch = "0";
                }

                packages.Add(new Package
                {
                    Name = parts[0],
                    Epoch = epoch,
                    Version = parts[2],
                    Release = parts[3],
                    Arch = parts[4],
                    InstalledSize = long.TryParse(parts[5], out var s) ? s : 0
                });
            }
            process.WaitForExit();
        }
        catch 
        {
            // If RPM isn't installed/working, return empty
        }

        return packages;
    }

    public static bool IsInstalled(string name, string sysRoot = "/")
    {
        var psi = new ProcessStartInfo
        {
            FileName = "rpm",
            Arguments = $"--root {sysRoot} -q {name}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        try
        {
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
            return proc?.ExitCode == 0;
        }
        catch { return false; }
    }
}