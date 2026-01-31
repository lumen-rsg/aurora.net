using System.Diagnostics;
using Aurora.Core.Contract;
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

    // Update the signature to accept a progress callback
    public async Task RunBuildFunctionAsync(string functionName, Action<string>? onLineReceived = null)
    {
        AnsiConsole.MarkupLine($"[cyan]=> Running {functionName}()...[/]");

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

        // Handle Standard Output
        process.OutputDataReceived += (_, args) => 
        { 
            if (args.Data != null) onLineReceived?.Invoke(args.Data); 
        };

        // Handle Errors (we'll treat these as normal logs for the spinner)
        process.ErrorDataReceived += (_, args) => 
        { 
            if (args.Data != null) onLineReceived?.Invoke(args.Data); 
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new Exception($"Function '{functionName}' failed with exit code {process.ExitCode}.");
        }
    }

    private void ConfigureEnvironment(ProcessStartInfo psi)
    {
        // --- Core Variables ---
        psi.Environment["srcdir"] = _srcDir;
        psi.Environment["pkgdir"] = _pkgDir;
        psi.Environment["startdir"] = _startDir;
        
        psi.Environment["pkgname"] = _manifest.Package.Name;
        psi.Environment["pkgver"] = _manifest.Package.Version.Split('-')[0]; // Before the hyphen
        psi.Environment["pkgrel"] = _manifest.Package.Version.Contains('-') ? _manifest.Package.Version.Split('-')[1] : "1";

        // --- Build Flags (from buildenv/*.sh logic) ---
        // For V1, we will set sensible defaults. A future V2 could parse makepkg.conf
        // TODO
        // to get CFLAGS, LDFLAGS, etc.

        // Simulating 'debugflags' and 'lto'
        var cflags = "-D_FORTIFY_SOURCE=2";
        if (_manifest.Build.Options.Contains("debug"))
        {
            cflags += " -g -ffile-prefix-map=${srcdir}=/usr/src/debug/${pkgname}";
        }
        if (_manifest.Build.Options.Contains("lto"))
        {
            cflags += " -flto";
        }
        
        psi.Environment["CFLAGS"] = cflags;
        psi.Environment["CXXFLAGS"] = cflags; // Usually the same
        psi.Environment["LDFLAGS"] = _manifest.Build.Options.Contains("lto") ? "-flto" : "";

        // Simulating 'makeflags'
        if (!_manifest.Build.Options.Contains("!makeflags"))
        {
            psi.Environment["MAKEFLAGS"] = "-j" + (Environment.ProcessorCount + 1);
        }

        // Simulating 'compiler' (ccache/distcc)
        var path = Environment.GetEnvironmentVariable("PATH");
        if (_manifest.Build.Environment.Contains("ccache"))
        {
            path = "/usr/lib/ccache/bin:" + path;
        }
        psi.Environment["PATH"] = path;
    }
}