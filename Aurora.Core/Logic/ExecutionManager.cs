using System.Diagnostics;
using System.Text;
using Aurora.Core.Contract;
using Aurora.Core.Logging;
using Aurora.Core.Logic.Build;
using Aurora.Core.Models;
using Aurora.Core.Parsing;
using Spectre.Console;

namespace Aurora.Core.Logic;

public class ExecutionManager
{
    private readonly string _buildDir;
    private readonly string _startDir;
    private readonly AuroraManifest _manifest;
    private readonly MakepkgConfig _sysConfig;
    
    // Paths exposed to the Bash environment
    private readonly string _srcDir;
    private readonly string _pkgDir;

    public ExecutionManager(string buildDir, string startDir, AuroraManifest manifest, MakepkgConfig sysConfig)
    {
        _buildDir = buildDir;
        _startDir = startDir;
        _manifest = manifest;
        _sysConfig = sysConfig;
        
        // Define standard makepkg directory structure
        _srcDir = Path.Combine(_buildDir, "src");
        // The pkgdir for the main package
        _pkgDir = Path.Combine(_buildDir, "pkg", manifest.Package.Name);
    }

    public void PrepareDirectories()
    {
        // Clean and create fresh directories for the build
        if (Directory.Exists(_srcDir)) Directory.Delete(_srcDir, true);
        if (Directory.Exists(_pkgDir)) Directory.Delete(_pkgDir, true);

        Directory.CreateDirectory(_srcDir);
        Directory.CreateDirectory(_pkgDir);
    }

    public async Task RunBuildFunctionAsync(string functionName, Action<string>? onLineReceived = null)
    {
        AnsiConsole.MarkupLine($"[cyan]=> Running {functionName}()...[/]");

        var logFile = Path.Combine(_buildDir, "build.log");
        var shimPath = Path.Combine(_srcDir, $"aurora_{functionName}_shim.sh");
        
        var shim = new StringBuilder();
        shim.AppendLine("#!/bin/bash");
        shim.AppendLine("set -e"); // Exit immediately on error
        shim.AppendLine("shopt -s nullglob globstar");
        
        // Export standard makepkg variables
        shim.AppendLine($"export srcdir='{_srcDir}'");
        shim.AppendLine($"export pkgdir='{_pkgDir}'");
        shim.AppendLine($"export startdir='{_startDir}'");
        
        // Define makepkg message stubs
        shim.AppendLine("msg() { echo \"  -> $1\"; }; msg2() { echo \"    $1\"; }");
        shim.AppendLine("warning() { echo \"  -> WARNING: $1\" >&2; }; error() { echo \"  -> ERROR: $1\" >&2; }");
        
        // Source the PKGBUILD to make functions and variables available
        shim.AppendLine($"source '{Path.Combine(_startDir, "PKGBUILD")}'");
        
        // Execute the requested function if it exists
        shim.AppendLine($"if type -t {functionName} | grep -q 'function'; then");
        shim.AppendLine($"    cd \"$srcdir\"");
        shim.AppendLine($"    {functionName}");
        shim.AppendLine("fi");

        await File.WriteAllTextAsync(shimPath, shim.ToString());
        File.SetUnixFileMode(shimPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"--noprofile --norc \"{shimPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _srcDir
        };

        ConfigureEnvironment(psi);

        using var process = Process.Start(psi);
        if (process == null) throw new Exception($"Failed to start process for {functionName}");

        void LogToFile(string? data)
        {
            if (data == null) return;
            onLineReceived?.Invoke(data);
            File.AppendAllText(logFile, data + Environment.NewLine);
        }

        process.OutputDataReceived += (_, args) => LogToFile(args.Data);
        process.ErrorDataReceived += (_, args) => LogToFile(args.Data);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        
        await process.WaitForExitAsync();

        try { File.Delete(shimPath); } catch {}

        if (process.ExitCode != 0)
        {
            throw new Exception($"Function '{functionName}' failed with exit code {process.ExitCode}. See build.log for details.");
        }
    }
    
    public async Task<AuroraManifest> RunPackageFunctionAsync(string functionName, AuroraManifest baseManifest, Action<string>? logAction = null)
    {
        string subPkgName = functionName.StartsWith("package_") ? functionName.Replace("package_", "") : baseManifest.Package.Name;
        string subPkgDir = Path.Combine(_buildDir, "pkg", subPkgName);
        
        if (Directory.Exists(subPkgDir)) Directory.Delete(subPkgDir, true);
        Directory.CreateDirectory(subPkgDir);

        var pkgbuildPath = Path.Combine(_startDir, "PKGBUILD");
        var logFile = Path.Combine(_buildDir, "build.log");
        var shimPath = Path.Combine(subPkgDir, "aurora_shim.sh");
        
        var shim = new StringBuilder();
        shim.AppendLine("#!/bin/bash");
        shim.AppendLine("set -e");
        
        // Stubs and exports for the packaging environment
        shim.AppendLine("msg() { :; }; msg2() { :; }; warning() { :; }; error() { :; }");
        shim.AppendLine($"export srcdir='{_srcDir}'");
        shim.AppendLine($"export pkgdir='{subPkgDir}'"); // Crucially, pkgdir points to the sub-package directory
        shim.AppendLine($"export pkgname='{subPkgName}'"); // And pkgname is set to the sub-package name
        shim.AppendLine($"export startdir='{_startDir}'");
        
        // Source the main PKGBUILD
        shim.AppendLine($"source '{pkgbuildPath}'");
        
        // Execute the specific package function (e.g., package_my-plugin())
        shim.AppendLine($"if type -t \"{functionName}\" &>/dev/null; then cd \"$srcdir\" && \"{functionName}\"; fi");

        // Helper to print array variables in .PKGINFO format
        shim.AppendLine("print_meta_arr() {");
        shim.AppendLine("  local key=$1");
        // Use a nameref for safe, robust indirect variable expansion
        shim.AppendLine("  local -n arr_ref=\"$key\" 2>/dev/null || return 0");
        shim.AppendLine("  for x in \"${arr_ref[@]}\"; do");
        shim.AppendLine("    [[ -n \"$x\" ]] && printf \"%s = %s\\n\" \"$key\" \"$x\";");
        shim.AppendLine("  done");
        shim.AppendLine("}");

        // Delimited block to capture override metadata in .PKGINFO format
        shim.AppendLine("echo '---AURORA_OVERRIDE_START---'");
        shim.AppendLine("printf \"pkgdesc = %s\\n\" \"${pkgdesc:-}\"");
        shim.AppendLine("print_meta_arr \"license\"");
        shim.AppendLine("print_meta_arr \"provides\"");
        shim.AppendLine("print_meta_arr \"conflict\"");
        shim.AppendLine("print_meta_arr \"replaces\"");
        shim.AppendLine("print_meta_arr \"depend\"");
        shim.AppendLine("print_meta_arr \"optdepend\"");
        shim.AppendLine("echo '---AURORA_OVERRIDE_END---'");

        await File.WriteAllTextAsync(shimPath, shim.ToString());
        File.SetUnixFileMode(shimPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var newManifest = CloneManifest(baseManifest);
        newManifest.Package.Name = subPkgName;

        var psi = new ProcessStartInfo {
            FileName = "/bin/bash",
            Arguments = $"\"{shimPath}\"",
            RedirectStandardOutput = true, 
            RedirectStandardError = true,
            UseShellExecute = false, 
            CreateNoWindow = true,
            WorkingDirectory = _srcDir
        };
        
        var finalPsi = FakerootHelper.WrapInFakeroot(psi);

        var overrideContent = new StringBuilder();
        bool captureMetadata = false;

        using var process = Process.Start(finalPsi);
        if (process == null) throw new Exception("Failed to start packaging process.");

        void HandleData(string? data)
        {
            if (data == null) return;
            File.AppendAllText(logFile, data + Environment.NewLine);

            var trimmed = data.Trim();
            if (trimmed == "---AURORA_OVERRIDE_START---") { captureMetadata = true; return; }
            if (trimmed == "---AURORA_OVERRIDE_END---") { captureMetadata = false; return; }

            if (captureMetadata) overrideContent.AppendLine(data);
            else logAction?.Invoke(data);
        }

        process.OutputDataReceived += (_, args) => HandleData(args.Data);
        process.ErrorDataReceived += (_, args) => HandleData(args.Data);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        try { File.Delete(shimPath); } catch {}

        if (process.ExitCode != 0)
        {
            throw new Exception($"Packaging function '{functionName}' failed with exit code {process.ExitCode}.");
        }
        
        // Parse the captured .PKGINFO-style text
        if (overrideContent.Length > 0)
        {
            var overrides = PkgInfoParser.Parse(overrideContent.ToString());
            ApplyOverrides(newManifest, overrides);
        }

        return newManifest;
    }
    
    private void ConfigureEnvironment(ProcessStartInfo psi)
    {
        // Start with a clean slate to avoid user environment pollution
        psi.Environment.Clear();

        // 1. Set a standard, minimal PATH
        var path = "/usr/local/bin:/usr/bin:/bin:/usr/local/sbin:/usr/sbin:/sbin";
        if (_manifest.Build.Options.Contains("ccache"))
        {
            path = "/usr/lib/ccache/bin:" + path;
        }
        psi.Environment["PATH"] = path;

        // 2. Set locale for predictable tool behavior (grep, sed, etc.)
        psi.Environment["LC_ALL"] = "C";
        psi.Environment["LANG"] = "C";
        psi.Environment["HOME"] = _buildDir;

        // 3. Set standard makepkg directory variables
        psi.Environment["srcdir"] = _srcDir;
        psi.Environment["pkgdir"] = _pkgDir;
        psi.Environment["startdir"] = _startDir;

        // 4. Inject system architecture from config (CRITICAL for configure scripts)
        psi.Environment["CARCH"] = _sysConfig.Arch;
        psi.Environment["CHOST"] = _sysConfig.Chost;

        // 5. Inject compiler flags from config
        psi.Environment["CFLAGS"] = _sysConfig.CFlags;
        psi.Environment["CXXFLAGS"] = _sysConfig.CxxFlags;
        psi.Environment["CPPFLAGS"] = _sysConfig.CppFlags;
        psi.Environment["LDFLAGS"] = _sysConfig.LdFlags;
        psi.Environment["MAKEFLAGS"] = _sysConfig.MakeFlags;

        // 6. Conditionally add debug or LTO flags based on PKGBUILD options
        if (_manifest.Build.Options.Contains("debug"))
        {
            var map = $"-ffile-prefix-map={_srcDir}=/usr/src/debug/{_manifest.Package.Name}";
            psi.Environment["CFLAGS"] += $" {_sysConfig.DebugCFlags} {map}";
            psi.Environment["CXXFLAGS"] += $" {_sysConfig.DebugCxxFlags} {map}";
        }

        if (_manifest.Build.Options.Contains("lto"))
        {
            psi.Environment["CFLAGS"] += $" {_sysConfig.LtoFlags}";
            psi.Environment["CXXFLAGS"] += $" {_sysConfig.LtoFlags}";
            psi.Environment["LDFLAGS"] += $" {_sysConfig.LtoFlags}";
        }
        
        // 7. Set packager identity
        psi.Environment["PACKAGER"] = _sysConfig.Packager;
    }
    
    private void ApplyOverrides(AuroraManifest target, AuroraManifest source)
    {
        if (!string.IsNullOrEmpty(source.Package.Description)) target.Package.Description = source.Package.Description;

        // Only replace lists if the sub-package function explicitly defined them
        if (source.Metadata.License.Any()) target.Metadata.License = source.Metadata.License;
        if (source.Metadata.Provides.Any()) target.Metadata.Provides = source.Metadata.Provides;
        if (source.Metadata.Conflicts.Any()) target.Metadata.Conflicts = source.Metadata.Conflicts;
        if (source.Metadata.Replaces.Any()) target.Metadata.Replaces = source.Metadata.Replaces;
        if (source.Dependencies.Runtime.Any()) target.Dependencies.Runtime = source.Dependencies.Runtime;
        if (source.Dependencies.Optional.Any()) target.Dependencies.Optional = source.Dependencies.Optional;
    }

    private AuroraManifest CloneManifest(AuroraManifest m)
    {
        // Return a deep copy to prevent sub-packages from modifying the base manifest
        return new AuroraManifest
        {
            Package = new PackageSection {
                Name = m.Package.Name,
                Version = m.Package.Version,
                Description = m.Package.Description,
                Architecture = m.Package.Architecture,
                Maintainer = m.Package.Maintainer,
                AllNames = new List<string>(m.Package.AllNames)
            },
            Metadata = new MetadataSection {
                Url = m.Metadata.Url,
                License = new List<string>(m.Metadata.License),
                Conflicts = new List<string>(m.Metadata.Conflicts),
                Provides = new List<string>(m.Metadata.Provides),
                Replaces = new List<string>(m.Metadata.Replaces),
                Backup = new List<string>(m.Metadata.Backup)
            },
            Dependencies = new DependencySection {
                Runtime = new List<string>(m.Dependencies.Runtime),
                Optional = new List<string>(m.Dependencies.Optional),
                Build = new List<string>(m.Dependencies.Build)
            },
            Build = new BuildSection {
                Options = new List<string>(m.Build.Options),
                Source = new List<string>(m.Build.Source),
                Sha256Sums = new List<string>(m.Build.Sha256Sums),
                NoExtract = new List<string>(m.Build.NoExtract)
            },
            Files = new FilesSection {
                PackageSize = m.Files.PackageSize,
                SourceHash = m.Files.SourceHash
            }
        };
    }
}