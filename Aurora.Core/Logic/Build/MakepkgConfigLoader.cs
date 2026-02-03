using System.Diagnostics;
using Aurora.Core.Models;
using Spectre.Console;

namespace Aurora.Core.Logic.Build;

public static class MakepkgConfigLoader
{
    public static async Task<MakepkgConfig> LoadAsync()
    {
        var config = new MakepkgConfig();

        // 1. Detect System Architecture (Fallback if not in config)
        // 'uname -m' is standard
        string defaultArch = "x86_64";
        try 
        {
            using var proc = Process.Start(new ProcessStartInfo("uname", "-m") { RedirectStandardOutput = true });
            if (proc != null) { 
                await proc.WaitForExitAsync(); 
                defaultArch = (await proc.StandardOutput.ReadToEndAsync()).Trim(); 
            }
        } 
        catch {}

        config.Arch = defaultArch;

        // 2. The Probe Script
        // Sources the config files and prints keys with a strict delimiter
        var shim = @"
#!/bin/bash
# Default fallbacks mimicking Arch Linux
CARCH=""$(uname -m)""
CHOST=""$CARCH-pc-linux-gnu""

# Source System Config
if [[ -f /etc/makepkg.conf ]]; then source /etc/makepkg.conf; fi

# Source User Config (Override)
if [[ -f ~/.makepkg.conf ]]; then source ~/.makepkg.conf; fi

# Helper
print_var() {
    printf ""%s|%s\n"" ""$1"" ""${!1}""
}

print_var ""CARCH""
print_var ""CHOST""
print_var ""CFLAGS""
print_var ""CXXFLAGS""
print_var ""CPPFLAGS""
print_var ""LDFLAGS""
print_var ""MAKEFLAGS""
print_var ""LTOFLAGS""
print_var ""DEBUG_CFLAGS""
print_var ""DEBUG_CXXFLAGS""
print_var ""PACKAGER""
";

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true, // Hide bash errors if config has weird stuff
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(psi);
            if (process == null) return config;

            await process.StandardInput.WriteAsync(shim);
            process.StandardInput.Close();

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('|', 2);
                if (parts.Length != 2) continue;

                var key = parts[0].Trim();
                var val = parts[1].Trim();

                if (string.IsNullOrEmpty(val)) continue;

                switch (key)
                {
                    case "CARCH": config.Arch = val; break;
                    case "CHOST": config.Chost = val; break;
                    case "CFLAGS": config.CFlags = val; break;
                    case "CXXFLAGS": config.CxxFlags = val; break;
                    case "CPPFLAGS": config.CppFlags = val; break;
                    case "LDFLAGS": config.LdFlags = val; break;
                    case "MAKEFLAGS": config.MakeFlags = val; break;
                    case "LTOFLAGS": config.LtoFlags = val; break;
                    case "DEBUG_CFLAGS": config.DebugCFlags = val; break;
                    case "DEBUG_CXXFLAGS": config.DebugCxxFlags = val; break;
                    case "PACKAGER": config.Packager = val; break;
                }
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning: Failed to load makepkg.conf: {ex.Message}. Using defaults.[/]");
        }

        return config;
    }
}