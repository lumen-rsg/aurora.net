using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Aurora.Core.Contract;
using Aurora.Core.Logic.Build;
using Aurora.Core.Logic.Extraction;
using Aurora.Core.Models;
using Aurora.Core.Parsing;
using Aurora.Core.Security;
using Spectre.Console;

namespace Aurora.Core.Logic.Providers;

public class ArchBuildProvider : IBuildProvider
{
    public string FormatName => "Arch Linux (PKGBUILD)";

    public bool CanHandle(string directory) => File.Exists(Path.Combine(directory, "PKGBUILD"));

    public async Task<AuroraManifest> GetManifestAsync(string directory)
    {
        var engine = new ArchBuildEngine(directory);
        return await engine.InspectPkgbuildAsync(Path.Combine(directory, "PKGBUILD")) 
               ?? throw new Exception("Failed to extract metadata from PKGBUILD");
    }

    public async Task FetchSourcesAsync(AuroraManifest manifest, string downloadDir, bool skipGpg, bool skipDownload, string startDir)
    {
        var validSources = manifest.Build.Source?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new();
        if (validSources.Count == 0) return;

        if (!Directory.Exists(downloadDir)) Directory.CreateDirectory(downloadDir);
        if (!skipDownload)
        {
            var sourceMgr = new SourceManager(startDir);
            await AnsiConsole.Progress()
                .Columns(new ProgressColumn[] { new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new DownloadedColumn(), new SpinnerColumn() })
                .StartAsync(async ctx => 
                {
                    foreach (var s in validSources)
                    {
                        var task = ctx.AddTask($"[grey]{Markup.Escape(new SourceEntry(s).FileName)}[/]");
                        await sourceMgr.FetchSourceAsync(new SourceEntry(s), downloadDir, (total, current) => {
                            if (total.HasValue) { task.MaxValue = total.Value; task.Value = current; }
                            else task.IsIndeterminate = true;
                        });
                        task.StopTask();
                    }
                });
        }

        new IntegrityManager().VerifyChecksums(manifest, downloadDir, startDir);
        if (!skipGpg) new SignatureVerifier().VerifySignatures(manifest, downloadDir, startDir);
    }

    public async Task BuildAsync(AuroraManifest manifest, string buildDir, string startDir, Action<string> logAction)
    {
        var sysConfig = await MakepkgConfigLoader.LoadAsync();
        var absoluteBuildDir = Path.GetFullPath(buildDir);
        var absoluteStartDir = Path.GetFullPath(startDir);
        var srcDir = Path.Combine(absoluteBuildDir, "src");

        if (Directory.Exists(srcDir)) Directory.Delete(srcDir, true);
        Directory.CreateDirectory(srcDir);
        
        var validSources = manifest.Build.Source?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new();
        if (validSources.Count > 0) await new SourceExtractor().ExtractAllAsync(manifest, Path.Combine(absoluteStartDir, "SRCDEST"), srcDir, absoluteStartDir);

        bool useFakeroot = FakerootHelper.IsAvailable();
        var scriptPath = Path.Combine(absoluteBuildDir, "aurora_build_script.sh");
        
        var scriptContent = GenerateMonolithicScript(manifest, sysConfig, srcDir, absoluteBuildDir, absoluteStartDir, useFakeroot);
        await File.WriteAllTextAsync(scriptPath, scriptContent);
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"--noprofile --norc \"{scriptPath}\"",
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = srcDir 
        };
        
        ConfigureBuildEnvironment(psi, sysConfig, manifest, srcDir, absoluteBuildDir, absoluteStartDir);
        
        var logFile = Path.Combine(buildDir, "build.log");
        using var process = Process.Start(psi);
        if (process == null) throw new Exception("Failed to start build process.");

        var subManifests = new Dictionary<string, AuroraManifest>();
        string? currentPkgName = null;
        var currentMetadata = new StringBuilder();
        bool capturingMetadata = false;

        void HandleOutput(string? line)
        {
            if (line == null) return;
            logAction(line);
            File.AppendAllText(logFile, line + Environment.NewLine);
            var trimmed = line.Trim();
            
            if (trimmed.StartsWith("---AURORA_PACKAGE_START|")) 
            {
                currentPkgName = trimmed.Split('|', 2)[1].Trim();
            }
            else if (trimmed == "---AURORA_METADATA_START---") 
            { 
                capturingMetadata = true; 
                currentMetadata.Clear(); 
            }
            else if (trimmed == "---AURORA_METADATA_END---")
            {
                capturingMetadata = false;
                if (!string.IsNullOrEmpty(currentPkgName))
                {
                    var m = CloneManifest(manifest);
                    m.Package.Name = currentPkgName;
                    ApplyOverrides(m, PkgInfoParser.Parse(currentMetadata.ToString()));
                    subManifests[currentPkgName] = m;
                }
            }
            else if (capturingMetadata) 
            {
                currentMetadata.AppendLine(line);
            }
        }
        
        process.OutputDataReceived += (_, args) => HandleOutput(args.Data);
        process.ErrorDataReceived += (_, args) => HandleOutput(args.Data);
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0) throw new Exception($"Build failed (code {process.ExitCode}). See build.log");

        AnsiConsole.MarkupLine("\n[bold magenta]Finalizing Artifacts...[/]");
        foreach (var (pkgName, subManifest) in subManifests)
        {
            var subPkgDir = Path.Combine(absoluteBuildDir, "pkg", pkgName);
            if (!Directory.Exists(subPkgDir)) continue;
            await ArtifactCreator.CreateAsync(subManifest, subPkgDir, absoluteStartDir);
        }
    }

    private string GenerateMonolithicScript(AuroraManifest m, MakepkgConfig c, string srcDir, string buildDir, string startDir, bool useFakeroot)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/bin/bash");
        sb.AppendLine("set -e");
        sb.AppendLine("shopt -s extglob");
        sb.AppendLine("shopt -u nullglob globstar");

        sb.AppendLine("msg() { echo \"==> $1\"; }; msg2() { echo \"  -> $1\"; };");
        sb.AppendLine("warning() { echo \"==> WARNING: $1\" >&2; }; error() { echo \"==> ERROR: $1\" >&2; exit 1; }");

        var pkgbuild = Path.Combine(startDir, "PKGBUILD");
        sb.AppendLine($"source '{pkgbuild}'");

        sb.AppendLine("run_prepare() { if type -t prepare &>/dev/null; then msg \"Running prepare()...\" && ( prepare ); fi; }");
        sb.AppendLine("run_build() { if type -t build &>/dev/null; then msg \"Running build()...\" && ( build ); fi; }");
        sb.AppendLine("run_check() { if type -t check &>/dev/null; then msg \"Running check()...\" && ( check ); fi; }");
        
        sb.AppendLine("scrape_metadata() {");
        sb.AppendLine("  echo '---AURORA_METADATA_START---'");
        sb.AppendLine("  printf 'pkgdesc = %s\\n' \"${pkgdesc:-}\"");
        sb.AppendLine("  local arr; for arr in license provides conflict replaces depend optdepend; do");
        sb.AppendLine("    local -n ref=\"$arr\" 2>/dev/null || continue;");
        sb.AppendLine("    for item in \"${ref[@]}\"; do [[ -n \"$item\" ]] && printf '%s = %s\\n' \"$arr\" \"$item\"; done;");
        sb.AppendLine("  done; echo '---AURORA_METADATA_END---';");
        sb.AppendLine("}");

        sb.AppendLine("cd \"$srcdir\"");
        sb.AppendLine("run_prepare");
        sb.AppendLine("run_build");
        if (m.Build.Options.Contains("check")) sb.AppendLine("run_check");

        sb.AppendLine("msg \"Starting packaging phase...\"");
        sb.AppendLine("ENV_FILE=$(mktemp)");
        sb.AppendLine("declare -p | grep -Ev '^declare -[a-z-]* (BASHOPTS|BASH_VERSINFO|EUID|PPID|SHELLOPTS|UID)=' > \"$ENV_FILE\" || true");

        sb.AppendLine("for pkg_name_entry in \"${pkgname[@]}\"; do");
        sb.AppendLine("  export CURRENT_PKG_NAME=\"$pkg_name_entry\"");
        sb.AppendLine("  export CURRENT_PKG_DIR=\"" + buildDir + "/pkg/$pkg_name_entry\"");
        sb.AppendLine("  rm -rf \"$CURRENT_PKG_DIR\" && mkdir -p \"$CURRENT_PKG_DIR\"");
        
        sb.AppendLine("  msg \"Packaging $CURRENT_PKG_NAME...\"");
        sb.AppendLine("  echo \"---AURORA_PACKAGE_START|$CURRENT_PKG_NAME\"");
        
        var fakerootPayload = "set -e; shopt -s extglob; shopt -u nullglob; source \"$1\"; " +
                              "export pkgname=\"$CURRENT_PKG_NAME\"; " +
                              "export pkgdir=\"$CURRENT_PKG_DIR\"; " +
                              "source '" + pkgbuild + "'; " +
                              "pkg_func=\"package_$pkgname\"; " +
                              "if ! type -t \"$pkg_func\" &>/dev/null; then pkg_func='package'; fi; " +
                              "cd \"$srcdir\" && \"$pkg_func\"";

        string fakerootCmd = useFakeroot ? "fakeroot --" : "";
        sb.AppendLine($"  {fakerootCmd} bash -c '{fakerootPayload}' -- \"$ENV_FILE\"");
        
        sb.AppendLine("  export pkgname=\"$CURRENT_PKG_NAME\" pkgdir=\"$CURRENT_PKG_DIR\"");
        sb.AppendLine("  ( source '" + pkgbuild + "'; scrape_metadata )");
        sb.AppendLine("done");
        sb.AppendLine("rm -f \"$ENV_FILE\"");

        return sb.ToString();
    }
    
    private void ConfigureBuildEnvironment(ProcessStartInfo psi, MakepkgConfig c, AuroraManifest m, string srcDir, string buildDir, string startDir)
    {
        // 1. PATH Management: Inherit system path but prioritize standard locations and Perl/Arch specific paths
        var hostPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        var standardPaths = "/usr/local/bin:/usr/bin:/bin:/usr/local/sbin:/usr/sbin:/sbin";
        var extraPaths = "/usr/bin/site_perl:/usr/bin/vendor_perl:/usr/bin/core_perl";
        
        var path = $"{standardPaths}:{extraPaths}:{hostPath}";
        if (m.Build.Options.Contains("ccache")) path = "/usr/lib/ccache/bin:" + path;
        
        psi.Environment["PATH"] = path;
        psi.Environment["SHELL"] = "/bin/bash";
        psi.Environment["LC_ALL"] = "C";
        psi.Environment["LANG"] = "C";
        psi.Environment["HOME"] = buildDir;
        psi.Environment["srcdir"] = srcDir;
        psi.Environment["startdir"] = startDir;
        psi.Environment["pkgdir"] = Path.Combine(buildDir, "pkg", m.Package.Name);
        psi.Environment["CARCH"] = c.Arch;
        psi.Environment["CHOST"] = c.Chost;

        string Sanitize(string val)
        {
            if (string.IsNullOrEmpty(val)) return val;
            var clean = val.Replace("-Wp,", "");
            if (clean.Contains("_FORTIFY_SOURCE"))
                return $"-U_FORTIFY_SOURCE {clean}";
            return clean;
        }

        psi.Environment["CFLAGS"] = Sanitize(c.CFlags);
        psi.Environment["CXXFLAGS"] = Sanitize(c.CxxFlags);
        psi.Environment["CPPFLAGS"] = Sanitize(c.CppFlags);
        psi.Environment["LDFLAGS"] = c.LdFlags;
        psi.Environment["MAKEFLAGS"] = c.MakeFlags;
        psi.Environment["PACKAGER"] = c.Packager;

        if (m.Build.Options.Contains("debug")) {
            var map = $"-ffile-prefix-map={srcDir}=/usr/src/debug/{m.Package.Name}";
            psi.Environment["CFLAGS"] += $" {c.DebugCFlags} {map}";
            psi.Environment["CXXFLAGS"] += $" {c.DebugCxxFlags} {map}";
        }
        if (m.Build.Options.Contains("lto")) {
            psi.Environment["CFLAGS"] += $" {c.LtoFlags}";
            psi.Environment["CXXFLAGS"] += $" {c.LtoFlags}";
            psi.Environment["LDFLAGS"] += $" {c.LtoFlags}";
        }
    }
    
    private void ApplyOverrides(AuroraManifest target, AuroraManifest source)
    {
        if (!string.IsNullOrEmpty(source.Package.Description)) target.Package.Description = source.Package.Description;
        if (source.Metadata.License.Any()) target.Metadata.License = source.Metadata.License;
        if (source.Metadata.Provides.Any()) target.Metadata.Provides = source.Metadata.Provides;
        if (source.Metadata.Conflicts.Any()) target.Metadata.Conflicts = source.Metadata.Conflicts;
        if (source.Metadata.Replaces.Any()) target.Metadata.Replaces = source.Metadata.Replaces;
        if (source.Dependencies.Runtime.Any()) target.Dependencies.Runtime = source.Dependencies.Runtime;
        if (source.Dependencies.Optional.Any()) target.Dependencies.Optional = source.Dependencies.Optional;
    }

    private AuroraManifest CloneManifest(AuroraManifest m) => new AuroraManifest {
        Package = new PackageSection { Name = m.Package.Name, Version = m.Package.Version, Description = m.Package.Description, Architecture = m.Package.Architecture, Maintainer = m.Package.Maintainer, AllNames = new List<string>(m.Package.AllNames) },
        Metadata = new MetadataSection { Url = m.Metadata.Url, License = new List<string>(m.Metadata.License), Conflicts = new List<string>(m.Metadata.Conflicts), Provides = new List<string>(m.Metadata.Provides), Replaces = new List<string>(m.Metadata.Replaces), Backup = new List<string>(m.Metadata.Backup) },
        Dependencies = new DependencySection { Runtime = new List<string>(m.Dependencies.Runtime), Optional = new List<string>(m.Dependencies.Optional), Build = new List<string>(m.Dependencies.Build) },
        Build = new BuildSection { Options = new List<string>(m.Build.Options), Source = new List<string>(m.Build.Source), Sha256Sums = new List<string>(m.Build.Sha256Sums), NoExtract = new List<string>(m.Build.NoExtract) },
        Files = new FilesSection { PackageSize = m.Files.PackageSize, SourceHash = m.Files.SourceHash }
    };
}