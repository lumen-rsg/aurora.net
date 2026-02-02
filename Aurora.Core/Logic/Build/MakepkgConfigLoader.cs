using System.Diagnostics;
using Aurora.Core.Models;

namespace Aurora.Core.Logic.Build;

public static class MakepkgConfigLoader
{
    public static async Task<MakepkgConfig> LoadAsync()
    {
        var config = new MakepkgConfig();

        // Probing shim to extract flags from the system
        var shim = @"
# Source system config
[[ -f /etc/makepkg.conf ]] && source /etc/makepkg.conf
# Source user config override
[[ -f ~/.makepkg.conf ]] && source ~/.makepkg.conf

echo ""CFLAGS=$CFLAGS""
echo ""CXXFLAGS=$CXXFLAGS""
echo ""LDFLAGS=$LDFLAGS""
echo ""MAKEFLAGS=$MAKEFLAGS""
echo ""DEBUG_CFLAGS=$DEBUG_CFLAGS""
echo ""DEBUG_CXXFLAGS=$DEBUG_CXXFLAGS""
echo ""CARCH=$CARCH""
echo ""PACKAGER=$PACKAGER""
";

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = "-s",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(psi);
            await process.StandardInput.WriteAsync(shim);
            process.StandardInput.Close();

            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) != null)
            {
                var parts = line.Split('=', 2);
                if (parts.Length < 2) continue;
                var key = parts[0];
                var val = parts[1];

                switch (key)
                {
                    case "CFLAGS": config.CFlags = val; break;
                    case "CXXFLAGS": config.CxxFlags = val; break;
                    case "LDFLAGS": config.LdFlags = val; break;
                    case "MAKEFLAGS": config.MakeFlags = val; break;
                    case "DEBUG_CFLAGS": config.DebugCFlags = val; break;
                    case "DEBUG_CXXFLAGS": config.DebugCxxFlags = val; break;
                    case "CARCH": config.Arch = val; break;
                    case "PACKAGER": config.Packager = val; break;
                }
            }
            await process.WaitForExitAsync();
        }
        catch { /* Fallback to defaults in model */ }

        return config;
    }
}