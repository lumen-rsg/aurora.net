using System.Diagnostics;
using System.Text;
using Aurora.Core.Contract;
using Aurora.Core.Models;
using Spectre.Console;

namespace Aurora.Core.Logic;

public class ArchBuildEngine
{
    private readonly string _workingDir;

    public ArchBuildEngine(string workingDir)
    {
        _workingDir = workingDir;
    }

    public async Task<AuroraManifest?> InspectPkgbuildAsync(string pkgbuildPath)
    {
        // We write the probe script to a temp file to avoid pipe escaping hell
        var probeScriptPath = Path.Combine(_workingDir, ".aurora_probe.sh");
        
        // This Bash script is the "Iron Logic" of extraction.
        // It sources the PKGBUILD safely and dumps variables in a deterministic format:
        // KEY|TYPE|VALUE
        var scriptContent = @"
#!/bin/bash
set -a # Export all variables
shopt -s nullglob globstar

# 1. Source the PKGBUILD safely
# We trap errors to ensure we don't crash silently
if ! source ""$1""; then
    echo ""FATAL|ERROR|Failed to source PKGBUILD""
    exit 1
fi

# 2. Helper to dump variables
# Format: NAME|TYPE|VALUE
dump_var() {
    local name=""$1""
    # Indirect reference to the variable
    local -n ref=""$name"" 2>/dev/null || return

    if [[ ""$(declare -p ""$name"" 2>/dev/null)"" =~ ""declare -a"" ]]; then
        # It is an array
        for val in ""${ref[@]}""; do
            printf ""%s|ARRAY|%s\n"" ""$name"" ""$val""
        done
    else
        # It is a scalar
        printf ""%s|SCALAR|%s\n"" ""$name"" ""$ref""
    fi
}

# 3. List of standard makepkg variables to extract
vars=(
    pkgname pkgver pkgrel epoch pkgdesc url arch license groups
    depends makedepends checkdepends optdepends
    provides conflicts replaces backup options source sha256sums noextract
    # Capture packager if set in env or file
    PACKAGER
)

echo ""__START_METADATA__""

# Handle split packages: pkgname might be an array
if [[ ""$(declare -p pkgname 2>/dev/null)"" =~ ""declare -a"" ]]; then
    for val in ""${pkgname[@]}""; do
        printf ""pkgname|ARRAY|%s\n"" ""$val""
    done
else
     printf ""pkgname|SCALAR|%s\n"" ""$pkgname""
fi

# Dump the rest
for v in ""${vars[@]}""; do
    [[ ""$v"" == ""pkgname"" ]] && continue
    dump_var ""$v""
done

echo ""__END_METADATA__""
";

        await File.WriteAllTextAsync(probeScriptPath, scriptContent);
        // Make executable
        File.SetUnixFileMode(probeScriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"--noprofile --norc \"{probeScriptPath}\" \"{pkgbuildPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _workingDir
        };
        
        // Sanitize env for the probe
        psi.Environment["LC_ALL"] = "C";

        using var process = Process.Start(psi);
        if (process == null) throw new Exception("Failed to start bash prober.");

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        // Cleanup
        try { File.Delete(probeScriptPath); } catch {}

        if (process.ExitCode != 0)
        {
            AnsiConsole.MarkupLine($"[red]PKGBUILD parsing failed:[/]");
            AnsiConsole.Write(new Panel(error).Header("Stderr").BorderColor(Color.Red));
            return null;
        }

        return ParseProbeOutput(output);
    }

    private AuroraManifest ParseProbeOutput(string output)
    {
        var manifest = new AuroraManifest();
        var lines = output.Split('\n');
        bool inBlock = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed == "__START_METADATA__") { inBlock = true; continue; }
            if (trimmed == "__END_METADATA__") { inBlock = false; break; }
            if (!inBlock || string.IsNullOrWhiteSpace(trimmed)) continue;

            // Split into 3 parts: KEY | TYPE | VALUE
            var parts = trimmed.Split('|', 3);
            if (parts.Length < 3) continue;

            var key = parts[0];
            var type = parts[1];
            var value = parts[2];

            switch (key)
            {
                case "pkgname":
                    // If multiple pkgnames exist (split package), the first one is the "base" name for this manifest context
                    if (string.IsNullOrEmpty(manifest.Package.Name)) manifest.Package.Name = value;
                    manifest.Package.AllNames.Add(value);
                    break;
                case "pkgver": manifest.Package.Version = value; break;
                case "pkgrel": manifest.Package.Version += $"-{value}"; break;
                case "epoch": 
                    if (!string.IsNullOrEmpty(value) && value != "0") 
                        manifest.Package.Version = $"{value}:{manifest.Package.Version}"; 
                    break;
                case "pkgdesc": manifest.Package.Description = value; break;
                case "url": manifest.Metadata.Url = value; break;
                case "arch": manifest.Package.Architecture = value; break; // Takes last if array, typical for Arch
                case "PACKAGER": manifest.Package.Maintainer = value; break;
                
                // Arrays
                case "license": manifest.Metadata.License.Add(value); break;
                case "depends": manifest.Dependencies.Runtime.Add(value); break;
                case "makedepends": manifest.Dependencies.Build.Add(value); break;
                case "optdepends": manifest.Dependencies.Optional.Add(value); break;
                case "provides": manifest.Metadata.Provides.Add(value); break;
                case "conflicts": manifest.Metadata.Conflicts.Add(value); break;
                case "replaces": manifest.Metadata.Replaces.Add(value); break;
                case "backup": manifest.Metadata.Backup.Add(value); break;
                case "options": manifest.Build.Options.Add(value); break;
                case "source": 
                    if (!string.IsNullOrWhiteSpace(value)) manifest.Build.Source.Add(value); 
                    break;
                case "sha256sums": 
                    if (!string.IsNullOrWhiteSpace(value)) manifest.Build.Sha256Sums.Add(value); 
                    break;
                case "noextract": 
                    if (!string.IsNullOrWhiteSpace(value)) manifest.Build.NoExtract.Add(value); 
                    break;
            }
        }

        return manifest;
    }
}