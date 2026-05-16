using System.Diagnostics;
using Aurora.Core.Logging;
using Aurora.Core.Models;

namespace Aurora.Core.State;

public static class RpmLocalDb
{
    public static List<Package> GetInstalledPackages(string sysRoot = "/")
    {
        var packages = new List<Package>();
        
        // RPM Query Format: NAME|EPOCH|VERSION|RELEASE|ARCH|SIZE|PROVIDES|REQUIRES|CONFLICTS (space-separated lists)
        // We use [%{TAG} ] to iterate through array tags with spaces between them.
        var psi = new ProcessStartInfo
        {
            FileName = "rpm",
            Arguments = "--root " + sysRoot + " -qa --qf \"%{NAME}|%{EPOCHNUM}|%{VERSION}|%{RELEASE}|%{ARCH}|%{SIZE}|[%{PROVIDES} ]|[%{REQUIRES} ]|[%{CONFLICTS} ]\\n\"",
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

                // Populate Provides from the 7th column
                if (parts.Length >= 7 && !string.IsNullOrWhiteSpace(parts[6]))
                {
                    var providesList = parts[6].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    pkg.Provides.AddRange(providesList);
                }

                // Populate Requires from the 8th column
                if (parts.Length >= 8 && !string.IsNullOrWhiteSpace(parts[7]))
                {
                    var requiresList = parts[7].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    pkg.Requires.AddRange(requiresList);
                }

                // Populate Conflicts from the 9th column
                if (parts.Length >= 9 && !string.IsNullOrWhiteSpace(parts[8]))
                {
                    var conflictsList = parts[8].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    pkg.Conflicts.AddRange(conflictsList);
                }

                packages.Add(pkg);
            }
            process.WaitForExit();
        }
        catch (Exception ex)
        {
            AuLogger.Error($"RpmLocalDb: failed to query installed packages: {ex.Message}");
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