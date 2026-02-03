using System.Diagnostics;
using Aurora.Core.Models;

namespace Aurora.Core.Logic.Build;

public static class MakepkgConfigLoader
{
    public static async Task<MakepkgConfig> LoadAsync()
    {
        var config = new MakepkgConfig();

        // Detect actual architecture
        try 
        {
            var psi = new ProcessStartInfo("uname", "-m") { RedirectStandardOutput = true, UseShellExecute = false };
            using var p = Process.Start(psi);
            if (p != null) { config.Arch = (await p.StandardOutput.ReadToEndAsync()).Trim(); }
        } catch {}

        // The Shim: Load system config and dump evaluated variables
        var shim = @"
[[ -f /etc/makepkg.conf ]] && source /etc/makepkg.conf
[[ -f ~/.makepkg.conf ]] && source ~/.makepkg.conf

# Re-evaluate CXXFLAGS if it contains literal $CFLAGS
eval CXXFLAGS=\""$CXXFLAGS\""

echo ""CHOST|$CHOST""
echo ""CFLAGS|$CFLAGS""
echo ""CXXFLAGS|$CXXFLAGS""
echo ""CPPFLAGS|$CPPFLAGS""
echo ""LDFLAGS|$LDFLAGS""
echo ""LTOFLAGS|$LTOFLAGS""
echo ""MAKEFLAGS|$MAKEFLAGS""
echo ""DEBUG_CFLAGS|$DEBUG_CFLAGS""
echo ""DEBUG_CXXFLAGS|$DEBUG_CXXFLAGS""
echo ""PACKAGER|$PACKAGER""
";

        try
        {
            var psi = new ProcessStartInfo("/bin/bash", "-c \"" + shim.Replace("\"", "\\\"") + "\"")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p != null)
            {
                string output = await p.StandardOutput.ReadToEndAsync();
                foreach (var line in output.Split('\n'))
                {
                    var parts = line.Split('|', 2);
                    if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1])) continue;
                    var key = parts[0]; var val = parts[1];
                    switch (key)
                    {
                        case "CHOST": config.Chost = val; break;
                        case "CFLAGS": config.CFlags = val; break;
                        case "CXXFLAGS": config.CxxFlags = val; break;
                        case "CPPFLAGS": config.CppFlags = val; break;
                        case "LDFLAGS": config.LdFlags = val; break;
                        case "LTOFLAGS": config.LtoFlags = val; break;
                        case "MAKEFLAGS": config.MakeFlags = val; break;
                        case "DEBUG_CFLAGS": config.DebugCFlags = val; break;
                        case "DEBUG_CXXFLAGS": config.DebugCxxFlags = val; break;
                        case "PACKAGER": config.Packager = val; break;
                    }
                }
            }
        } catch { /* Fallback to built-in defaults */ }

        return config;
    }
}