using System.Diagnostics;
using Aurora.Core.Models;

namespace Aurora.Core.State;

public static class RpmLocalDb
{
    public static List<Package> GetInstalledPackages(string sysRoot = "/")
    {
        var packages = new List<Package>();
        
        // RPM Query Format: NAME|EPOCH|VERSION|RELEASE|ARCH|SIZE|PROVIDES(space separated)
        // We use [%{PROVIDES} ] to iterate through all provides and put a space between them.
        var psi = new ProcessStartInfo
        {
            FileName = "rpm",
            Arguments = "--root " + sysRoot + " -qa --qf \"%{NAME}|%{EPOCHNUM}|%{VERSION}|%{RELEASE}|%{ARCH}|%{SIZE}|[%{PROVIDES} ]\\n\"",
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

                string epoch = parts[1];
                if (epoch == "(none)" || string.IsNullOrWhiteSpace(epoch)) epoch = "0";

                var pkg = new Package
                {
                    Name = parts[0],
                    Epoch = epoch,
                    Version = parts[2],
                    Release = parts[3],
                    Arch = parts[4],
                    InstalledSize = long.TryParse(parts[5], out var s) ? s : 0
                };

                // FIX: Populate Provides from the 7th column
                if (parts.Length >= 7 && !string.IsNullOrWhiteSpace(parts[6]))
                {
                    // Split the space-separated provides list
                    var providesList = parts[6].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    pkg.Provides.AddRange(providesList);
                }

                packages.Add(pkg);
            }
            process.WaitForExit();
        }
        catch 
        {
            // Silent fail for bootstrap
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