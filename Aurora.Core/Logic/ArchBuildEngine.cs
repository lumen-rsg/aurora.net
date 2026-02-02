using System.Diagnostics;
using System.Text;
using Aurora.Core.Contract;
using Aurora.Core.Parsing;
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

    /// <summary>
    /// Executes a Bash shim to source the PKGBUILD and extract all variables
    /// defined in the makepkg schema.
    /// </summary>
    public async Task<AuroraManifest?> InspectPkgbuildAsync(string pkgbuildPath)
    {
        // 1. We define stubs for common makepkg functions so the PKGBUILD doesn't 
        // crash when sourced if it calls them globally (though rare).
        // 2. We use 'set +e' to ensure sourcing continues even if the script has minor issues.
        // 3. We quote all values to ensure the YAML parser doesn't choke on colons/quotes.
        var shim = @"
# Stubs for makepkg internal functions
msg() { :; }; msg2() { :; }; warning() { :; }; error() { :; }
gettext() { echo ""$1""; }; 

# Source the file, but don't exit on errors
set +e
source ""$1""

# Helper to construct full version
fullver=""$pkgver-$pkgrel""
[[ -n ""${epoch-}"" ]] && fullver=""$epoch:$fullver""

echo ""package:""
# Use printf to handle special characters and ensure quoting
printf ""  name: \""%s\""\n"" ""${pkgname[0]}""
printf ""  version: \""%s\""\n"" ""$fullver""
printf ""  description: \""%s\""\n"" ""${pkgdesc-}""
printf ""  architecture: \""%s\""\n"" ""${arch[0]}""
printf ""  maintainer: \""%s\""\n"" ""${PACKAGER:-Unknown Packager}""

echo ""  all_names:""
for n in ""${pkgname[@]}""; do 
    [[ -n ""$n"" ]] && printf ""    - \""%s\""\n"" ""$n""
done

echo ""metadata:""
printf ""  url: \""%s\""\n"" ""${url-}""
echo ""  license:""
for l in ""${license[@]-}""; do [[ -n ""$l"" ]] && printf ""    - \""%s\""\n"" ""$l""; done

echo ""dependencies:""
echo ""  runtime:""
for d in ""${depends[@]-}""; do [[ -n ""$d"" ]] && printf ""    - \""%s\""\n"" ""$d""; done
echo ""  build:""
for d in ""${makedepends[@]-}""; do [[ -n ""$d"" ]] && printf ""    - \""%s\""\n"" ""$d""; done

echo ""build:""
echo ""  options:""
for o in ""${options[@]-}""; do [[ -n ""$o"" ]] && printf ""    - \""%s\""\n"" ""$o""; done
echo ""  source:""
for s in ""${source[@]-}""; do [[ -n ""$s"" ]] && printf ""    - \""%s\""\n"" ""$s""; done
echo ""  sha256sums:""
for s in ""${sha256sums[@]-}""; do [[ -n ""$s"" ]] && printf ""    - \""%s\""\n"" ""$s""; done
";

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            // We use --noprofile to ensure a clean environment
            Arguments = "--noprofile -s -- " + $"\"{pkgbuildPath}\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _workingDir
        };

        try
        {
            using var process = Process.Start(psi);
            if (process == null) throw new Exception("Could not start bash process.");

            // Pipe the shim to stdin
            await process.StandardInput.WriteAsync(shim);
            process.StandardInput.Close();

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            // If bash itself fails (syntax error in sourcing), show stderr
            if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
            {
                throw new Exception($"Bash prober failed to start:\n{error}");
            }

            // If there's output, we try to parse it even if exit code was non-zero
            // because some PKGBUILDs return non-zero after sourcing due to 
            // the last executed command in the file.
            return ManifestParser.Parse(output);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error probing PKGBUILD:[/] {Markup.Escape(ex.Message)}");
            return null;
        }
    }
}