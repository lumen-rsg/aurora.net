using System.Diagnostics;
using System.Text;
using Aurora.Core.Contract;
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

    public ExecutionManager(string buildDir, string startDir, AuroraManifest manifest)
    {
        _buildDir = buildDir;
        _startDir = startDir;
        _manifest = manifest;
        
        // Define standard makepkg directory structure
        _srcDir = Path.Combine(_buildDir, "src");
        _pkgDir = Path.Combine(_buildDir, "pkg", manifest.Package.Name);
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

        // Path for the log file
        var logFile = Path.Combine(_buildDir, "build.log");

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _srcDir
        };

        ConfigureEnvironment(psi);

        var pkgbuildPath = Path.Combine(_startDir, "PKGBUILD");
        var shim = $@"
            export srcdir='{_srcDir}'
            export pkgdir='{_pkgDir}'
            export startdir='{_startDir}'
            source '{pkgbuildPath}'
            if type -t {functionName} &>/dev/null; then
                cd ""$srcdir""
                {functionName}
            fi";

        psi.Arguments = $"-c \"{shim.Replace("\"", "\\\"")}\"";

        using var process = Process.Start(psi);
        if (process == null) throw new Exception($"Failed to start process for {functionName}");

        // Use a Thread-safe way to append to the log file
        void LogToFile(string? data)
        {
            if (data == null) return;
            onLineReceived?.Invoke(data);
            
            // Append to build.log
            File.AppendAllText(logFile, data + Environment.NewLine);
        }

        process.OutputDataReceived += (_, args) => LogToFile(args.Data);
        process.ErrorDataReceived += (_, args) => LogToFile(args.Data);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            // Throw a specific exception that includes the log path
            throw new Exception($"Function '{functionName}' failed with exit code {process.ExitCode}. See {logFile} for details.");
        }
    }

    private void ConfigureEnvironment(ProcessStartInfo psi)
    {
        psi.Environment["srcdir"] = _srcDir;
        psi.Environment["pkgdir"] = _pkgDir;
        psi.Environment["startdir"] = _startDir;
        
        // Use the system's actual build flags
        var cflags = _sysConfig.CFlags;
        var cxxflags = _sysConfig.CxxFlags;

        // If 'debug' is in options, append the debug flags
        if (_manifest.Build.Options.Contains("debug"))
        {
            cflags += " " + _sysConfig.DebugCFlags;
            cxxflags += " " + _sysConfig.DebugCxxFlags;
            
            // Add the path remapping needed for debug symbols
            var map = $"-ffile-prefix-map={_srcDir}=/usr/src/debug/{_manifest.Package.Name}";
            cflags += " " + map;
            cxxflags += " " + map;
        }

        psi.Environment["CFLAGS"] = cflags;
        psi.Environment["CXXFLAGS"] = cxxflags;
        psi.Environment["LDFLAGS"] = _sysConfig.LdFlags;
        psi.Environment["MAKEFLAGS"] = _sysConfig.MakeFlags;
        
        // Pass PATH
        psi.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH");
    }
    
public async Task<AuroraManifest> RunPackageFunctionAsync(string functionName, AuroraManifest baseManifest, Action<string>? logAction = null)
    {
        string subPkgName = functionName.StartsWith("package_") ? functionName.Replace("package_", "") : baseManifest.Package.Name;
        string subPkgDir = Path.Combine(_buildDir, "pkg", subPkgName);
        Directory.CreateDirectory(subPkgDir);

        var pkgbuildPath = Path.Combine(_startDir, "PKGBUILD");

        var shim = $@"
            export srcdir='{_srcDir}'
            export pkgdir='{subPkgDir}'
            export pkgname='{subPkgName}'
            export startdir='{_startDir}'
            source '{pkgbuildPath}'

            if type -t {functionName} &>/dev/null; then
                cd ""$srcdir""
                {functionName}
            fi

            echo """"---AURORA_OVERRIDE_START---""""
            echo """"package:""""
            echo """"  description: $pkgdesc""""
            # Capture split-package maintainer if defined
            echo """"  maintainer: ${{PACKAGER:-Unknown Packager}}"""" 

            echo """"metadata:""""
            echo """"  license:""""
            for l in """"${{{{license[@]-}}}}""""; do [[ -n """"$l"""" ]] && echo """"    - $l""""; done
            echo """"  provides:""""
            for x in ""${{provides[@]-}}""; do [[ -n ""$x"" ]] && echo ""    - $x""; done
            echo ""  conflicts:""
            for x in ""${{conflicts[@]-}}""; do [[ -n ""$x"" ]] && echo ""    - $x""; done
            echo ""  replaces:""
            for x in ""${{replaces[@]-}}""; do [[ -n ""$x"" ]] && echo ""    - $x""; done
            echo ""dependencies:""
            echo ""  runtime:""
            for x in ""${{depends[@]-}}""; do [[ -n ""$x"" ]] && echo ""    - $x""; done
            echo ""  optional:""
            for x in ""${{optdepends[@]-}}""; do [[ -n ""$x"" ]] && echo ""    - $x""; done
            echo ""---AURORA_OVERRIDE_END---""";

        var newManifest = CloneManifest(baseManifest);
        newManifest.Package.Name = subPkgName;

        var psi = new ProcessStartInfo {
            FileName = "/bin/bash",
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = _srcDir
        };
        psi.Arguments = $"-c \"{shim.Replace("\"", "\\\"")}\"";
        
        var finalPsi = FakerootHelper.WrapInFakeroot(psi);
        AnsiConsole.MarkupLine("[grey]=> Entering fakeroot environment...[/]");

        var overrideYaml = new StringBuilder();
        bool captureMetadata = false;

        using var process = Process.Start(finalPsi);
        process.OutputDataReceived += (_, args) => {
            if (args.Data == null) return;
            if (args.Data == "---AURORA_OVERRIDE_START---") { captureMetadata = true; return; }
            if (args.Data == "---AURORA_OVERRIDE_END---") { captureMetadata = false; return; }

            if (captureMetadata) overrideYaml.AppendLine(args.Data);
            else logAction?.Invoke(args.Data);
        };
        process.ErrorDataReceived += (_, args) => { if (args.Data != null) logAction?.Invoke(args.Data); };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        // Parse the captured YAML and apply it to our sub-manifest
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