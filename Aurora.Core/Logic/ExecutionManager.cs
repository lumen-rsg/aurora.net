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
    
    // Paths exposed to the Bash environment
    private readonly string _srcDir;
    private readonly string _pkgDir;
    private MakepkgConfig _sysConfig;

    public ExecutionManager(string buildDir, string startDir, AuroraManifest manifest, MakepkgConfig sysConfig)
    {
        _buildDir = buildDir;
        _startDir = startDir;
        _manifest = manifest;
        
        // Define standard makepkg directory structure
        _srcDir = Path.Combine(_buildDir, "src");
        _pkgDir = Path.Combine(_buildDir, "pkg", manifest.Package.Name);
        _sysConfig = sysConfig;
    }

    public void PrepareDirectories()
    {
        // Clean and create fresh directories
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
        shim.AppendLine("set -e");
        
        // Export variables for the script
        shim.AppendLine($"export srcdir='{_srcDir}'");
        shim.AppendLine($"export pkgdir='{_pkgDir}'");
        shim.AppendLine($"export startdir='{_startDir}'");
        
        // Helpers
        shim.AppendLine("msg() { echo \"  -> $1\"; }; msg2() { echo \"    $1\"; }; warning() { echo \"  -> WARNING: $1\" >&2; }; error() { echo \"  -> ERROR: $1\" >&2; }");
        
        shim.AppendLine($"source '{Path.Combine(_startDir, "PKGBUILD")}'");
        
        shim.AppendLine($"if type -t {functionName} &>/dev/null; then");
        shim.AppendLine($"    cd \"$srcdir\"");
        shim.AppendLine($"    {functionName}");
        shim.AppendLine("fi");

        File.WriteAllText(shimPath, shim.ToString());
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(shimPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

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

        // 1. Setup Environment (Sets PATH, LC_ALL, and M4)
        ConfigureEnvironment(psi);
        
        // 2. Ensure CONFIG_SHELL is set (Autoconf needs this to be consistent)
        psi.Environment["CONFIG_SHELL"] = "/bin/bash";

        // DELETE THIS BLOCK IF IT EXISTS IN YOUR FILE:
        // if (psi.Environment.ContainsKey("M4")) psi.Environment.Remove("M4");  <-- DELETE THIS

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
            throw new Exception($"Function '{functionName}' failed with exit code {process.ExitCode}. See {logFile} for details.");
        }
    }

    private void ConfigureEnvironment(ProcessStartInfo psi)
    {
        // --- 1. Start with a clean slate for build-related vars ---
        // makepkg unsets these to ensure a reproducible environment.
        psi.Environment.Remove("ACLOCAL_PATH");
        psi.Environment.Remove("AUTOMAKE_FLAGS");
        psi.Environment.Remove("AUTOCONF_FLAGS");
        psi.Environment.Remove("GETTEXT_PATH");

        // --- 2. Set core Aurora/makepkg variables ---
        psi.Environment["srcdir"] = _srcDir;
        psi.Environment["pkgdir"] = _pkgDir;
        psi.Environment["startdir"] = _startDir;
        psi.Environment["LC_ALL"] = "C";
        psi.Environment["LANG"] = "C";
        
        // --- 3. Set compiler flags from loaded config ---
        var cflags = _sysConfig.CFlags;
        var cxxflags = _sysConfig.CxxFlags;

        if (_manifest.Build.Options.Contains("debug"))
        {
            cflags += " " + _sysConfig.DebugCFlags;
            cxxflags += " " + _sysConfig.DebugCxxFlags;
            var map = $"-ffile-prefix-map={_srcDir}=/usr/src/debug/{_manifest.Package.Name}";
            cflags += " " + map;
            cxxflags += " " + map;
        }

        psi.Environment["CFLAGS"] = cflags;
        psi.Environment["CXXFLAGS"] = cxxflags;
        psi.Environment["LDFLAGS"] = _sysConfig.LdFlags;
        psi.Environment["MAKEFLAGS"] = _sysConfig.MakeFlags;
        
        // --- 4. Set a robust PATH ---
        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        var requiredPaths = new List<string> 
        { 
            "/usr/local/bin", "/usr/bin", "/bin", 
            "/usr/local/sbin", "/usr/sbin", "/sbin" 
        };

        if (_manifest.Build.Environment.Contains("ccache"))
        {
            requiredPaths.Insert(0, "/usr/lib/ccache/bin");
        }

        var existingParts = currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in existingParts)
        {
            if (!requiredPaths.Contains(part)) requiredPaths.Add(part);
        }
        psi.Environment["PATH"] = string.Join(Path.PathSeparator, requiredPaths);

        // --- 5. Explicitly set tool paths ---
        string? m4Path = FindExecutable("m4", requiredPaths);
        if (!string.IsNullOrEmpty(m4Path))
        {
            psi.Environment["M4"] = m4Path;
        }
    }

    private string? FindExecutable(string name, List<string> paths)
    {
        foreach (var path in paths)
        {
            var fullPath = Path.Combine(path, name);
            if (File.Exists(fullPath)) return fullPath;
        }
        return null;
    }
    
public async Task<AuroraManifest> RunPackageFunctionAsync(string functionName, AuroraManifest baseManifest, Action<string>? logAction = null)
    {
        string subPkgName = functionName.StartsWith("package_") ? functionName.Replace("package_", "") : baseManifest.Package.Name;
        string subPkgDir = Path.Combine(_buildDir, "pkg", subPkgName);
        
        if (Directory.Exists(subPkgDir)) Directory.Delete(subPkgDir, true);
        Directory.CreateDirectory(subPkgDir);

        var pkgbuildPath = Path.Combine(_startDir, "PKGBUILD");
        var logFile = Path.Combine(_buildDir, "build.log");

        // --- NEW STRATEGY: Write shim to a temporary file ---
        var shimPath = Path.Combine(subPkgDir, "aurora_shim.sh");
        var shim = new StringBuilder();
        shim.AppendLine("#!/bin/bash");
        shim.AppendLine("msg() { :; }; msg2() { :; }; warning() { :; }; error() { :; }");
        shim.AppendLine($"export srcdir='{_srcDir}'");
        shim.AppendLine($"export pkgdir='{subPkgDir}'");
        shim.AppendLine($"export pkgname='{subPkgName}'");
        shim.AppendLine($"export startdir='{_startDir}'");
        shim.AppendLine($"source '{pkgbuildPath}'");
        
        shim.AppendLine($"if type -t \"{functionName}\" &>/dev/null; then cd \"$srcdir\" && \"{functionName}\"; fi");

        shim.AppendLine("print_meta_arr() {");
        shim.AppendLine("  local key=$1; local var=\"${1}[@]\"; echo \"  $key:\"");
        shim.AppendLine("  for x in \"${!var}\"; do [[ -n \"$x\" ]] && printf \"    - \\\"%s\\\"\\n\" \"$x\"; done");
        shim.AppendLine("}");

        shim.AppendLine("echo '---AURORA_OVERRIDE_START---'");
        shim.AppendLine("echo 'package:'");
        shim.AppendLine("printf \"  description: \\\"%s\\\"\\n\" \"$pkgdesc\"");
        shim.AppendLine("echo 'metadata:'");
        shim.AppendLine("print_meta_arr \"license\"");
        shim.AppendLine("print_meta_arr \"provides\"");
        shim.AppendLine("print_meta_arr \"conflicts\"");
        shim.AppendLine("print_meta_arr \"replaces\"");
        shim.AppendLine("echo 'dependencies:'");
        shim.AppendLine("print_meta_arr \"depends\"");
        shim.AppendLine("print_meta_arr \"optdepends\"");
        shim.AppendLine("echo '---AURORA_OVERRIDE_END---'");

        File.WriteAllText(shimPath, shim.ToString());
        
        // Ensure the shim is executable
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(shimPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var newManifest = CloneManifest(baseManifest);
        newManifest.Package.Name = subPkgName;

        var psi = new ProcessStartInfo {
            FileName = "/bin/bash",
            Arguments = $"\"{shimPath}\"", // Execute the shim file
            RedirectStandardOutput = true, 
            RedirectStandardError = true,
            UseShellExecute = false, 
            CreateNoWindow = true,
            WorkingDirectory = _srcDir
        };
        
        var finalPsi = FakerootHelper.WrapInFakeroot(psi);

        var overrideYaml = new StringBuilder();
        bool captureMetadata = false;

        using var process = Process.Start(finalPsi);
        if (process == null) throw new Exception("Failed to start packaging process.");

        void HandleData(string? data)
        {
            if (data == null) return;
            // Write everything to the persistent log file
            File.AppendAllText(logFile, data + Environment.NewLine);

            var trimmed = data.Trim();
            if (trimmed == "---AURORA_OVERRIDE_START---") { captureMetadata = true; return; }
            if (trimmed == "---AURORA_OVERRIDE_END---") { captureMetadata = false; return; }

            if (captureMetadata) overrideYaml.AppendLine(data);
            else logAction?.Invoke(data);
        }

        process.OutputDataReceived += (_, args) => HandleData(args.Data);
        process.ErrorDataReceived += (_, args) => HandleData(args.Data);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        // Cleanup the temporary shim
        try { File.Delete(shimPath); } catch { }

        if (process.ExitCode != 0)
        {
            throw new Exception($"Packaging function '{functionName}' failed with exit code {process.ExitCode}.");
        }

        if (overrideYaml.Length > 0)
        {
            var overrides = ManifestParser.Parse(overrideYaml.ToString());
            ApplyOverrides(newManifest, overrides);
        }

        return newManifest;
    }

    private void ApplyOverrides(AuroraManifest target, AuroraManifest source)
    {
        if (!string.IsNullOrEmpty(source.Package.Description)) target.Package.Description = source.Package.Description;
        if (!string.IsNullOrEmpty(source.Package.Maintainer)) target.Package.Maintainer = source.Package.Maintainer;

        // Update licenses if specifically defined in the sub-function
        if (source.Metadata.License.Count > 0) target.Metadata.License = source.Metadata.License;
        
        // Only override lists if the sub-function actually defined them
        if (source.Metadata.Provides.Count > 0) target.Metadata.Provides = source.Metadata.Provides;
        if (source.Metadata.Conflicts.Count > 0) target.Metadata.Conflicts = source.Metadata.Conflicts;
        if (source.Metadata.Replaces.Count > 0) target.Metadata.Replaces = source.Metadata.Replaces;
        if (source.Dependencies.Runtime.Count > 0) target.Dependencies.Runtime = source.Dependencies.Runtime;
        if (source.Dependencies.Optional.Count > 0) target.Dependencies.Optional = source.Dependencies.Optional;
    }
    

    private AuroraManifest CloneManifest(AuroraManifest m)
    {
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
                Replaces = new List<string>(m.Metadata.Replaces)
            },
            Dependencies = new DependencySection {
                Runtime = new List<string>(m.Dependencies.Runtime),
                Optional = new List<string>(m.Dependencies.Optional),
                Build = new List<string>(m.Dependencies.Build)
            },
            Build = new BuildSection {
                Options = new List<string>(m.Build.Options),
                Source = new List<string>(m.Build.Source),
                Sha256Sums = new List<string>(m.Build.Sha256Sums)
            },
            Files = new FilesSection {
                PackageSize = m.Files.PackageSize,
                SourceHash = m.Files.SourceHash
            }
        };
    }
}