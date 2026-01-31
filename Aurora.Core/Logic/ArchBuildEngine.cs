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
        // We use a clean, non-verbatim string for the Bash script to avoid confusion
        // Note: we use ${var-} syntax to handle unset variables without crashing
        var shim = @"
source ""$1""

# Helper to construct full version
fullver=""$pkgver-$pkgrel""
[[ -n ""${epoch-}"" ]] && fullver=""$epoch:$fullver""

echo ""package:""
echo ""  name: ${pkgname[0]}""
echo ""  version: $fullver""
echo ""  description: ${pkgdesc-}""
echo ""  architecture: ${arch[0]}""
echo ""  maintainer: ${packager-}""

echo ""metadata:""
echo ""  url: ${url-}""

echo ""dependencies:""
echo ""  runtime:""
for d in ""${depends[@]-}""; do [[ -n ""$d"" ]] && echo ""    - $d""; done
echo ""  build:""
for d in ""${makedepends[@]-}""; do [[ -n ""$d"" ]] && echo ""    - $d""; done

echo ""build:""
echo ""  options:""
for o in ""${options[@]-}""; do [[ -n ""$o"" ]] && echo ""    - $o""; done
echo ""  source:""
# Removed the manual escaped quotes around $s
for s in ""${source[@]-}""; do [[ -n ""$s"" ]] && echo ""    - $s""; done
echo ""  sha256sums:""
for s in ""${sha256sums[@]-}""; do [[ -n ""$s"" ]] && echo ""    - $s""; done
";

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            // --noprofile --norc: Speed up and isolation
            // -s: Read commands from stdin, remaining args go to $1, $2...
            Arguments = "--noprofile --norc -s -- " + $"\"{pkgbuildPath}\"",
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

            // Pipe the script to bash
            await process.StandardInput.WriteAsync(shim);
            process.StandardInput.Close();

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                throw new Exception($"Bash prober failed: {error}");
            }

            // Parse the output using our ManifestParser
            return ManifestParser.Parse(output);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error probing PKGBUILD:[/] {Markup.Escape(ex.Message)}");
            return null;
        }
    }
}