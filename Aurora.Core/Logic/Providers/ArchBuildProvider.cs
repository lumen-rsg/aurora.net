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

    public bool CanHandle(string directory)
    {
        return File.Exists(Path.Combine(directory, "PKGBUILD"));
    }

    public async Task<AuroraManifest> GetManifestAsync(string directory)
    {
        var engine = new ArchBuildEngine(directory);
        var pkgbuildPath = Path.Combine(directory, "PKGBUILD");
        
        var manifest = await engine.InspectPkgbuildAsync(pkgbuildPath);
        if (manifest == null) throw new Exception("Failed to extract metadata from PKGBUILD");
        
        return manifest;
    }

    public async Task FetchSourcesAsync(AuroraManifest manifest, string downloadDir, bool skipGpg, bool skipDownload, string startDir)
    {
        var validSources = manifest.Build.Source?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new();

        if (validSources.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No sources to download (Meta-package).[/]");
            return;
        }

        if (!Directory.Exists(downloadDir)) Directory.CreateDirectory(downloadDir);

        if (skipDownload)
        {
            AnsiConsole.MarkupLine("[yellow]Skipping download phase (using cached sources).[/]");
        }
        else
        {
            var sourceMgr = new SourceManager(startDir);
            AnsiConsole.MarkupLine("[bold]Fetching sources...[/]");

            await AnsiConsole.Progress()
                .Columns(new ProgressColumn[] 
                {
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new DownloadedColumn(),
                    new SpinnerColumn(),
                })
                .StartAsync(async ctx => 
                {
                    foreach (var sourceStr in validSources)
                    {
                        var entry = new SourceEntry(sourceStr);
                        var task = ctx.AddTask($"[grey]{Markup.Escape(entry.FileName)}[/]");
                        await sourceMgr.FetchSourceAsync(entry, downloadDir, (total, current) => 
                        {
                            if (total.HasValue && total.Value > 0)
                            {
                                task.MaxValue = total.Value;
                                task.Value = current;
                            }
                            else
                            {
                                task.IsIndeterminate = true;
                            }
                        });
                        task.StopTask();
                    }
                });
        }

        var integrity = new IntegrityManager();
        integrity.VerifyChecksums(manifest, downloadDir, startDir);

        if (!skipGpg)
        {
            var sigVerifier = new SignatureVerifier();
            sigVerifier.VerifySignatures(manifest, downloadDir, startDir);
        }
    }

    public async Task BuildAsync(AuroraManifest manifest, string buildDir, string startDir, Action<string> logAction)
    {
        var sysConfig = await MakepkgConfigLoader.LoadAsync();
        var absoluteBuildDir = Path.GetFullPath(buildDir);
        var absoluteStartDir = Path.GetFullPath(startDir);
        var srcDir = Path.Combine(absoluteBuildDir, "src");

        if (Directory.Exists(srcDir)) Directory.Delete(srcDir, true);
        Directory.CreateDirectory(srcDir);
        
        var cacheDir = Path.Combine(absoluteStartDir, "SRCDEST");

        var validSources = manifest.Build.Source?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new();
        if (validSources.Count > 0)
        {
            var extractor = new SourceExtractor();
            await extractor.ExtractAllAsync(manifest, cacheDir, srcDir, absoluteStartDir);
        }

        bool useFakeroot = FakerootHelper.IsAvailable();

        var scriptPath = Path.Combine(absoluteBuildDir, "aurora_build_script.sh");
        var scriptContent = GenerateMonolithicScript(manifest, sysConfig, srcDir, absoluteBuildDir, absoluteStartDir, useFakeroot);
        await File.WriteAllTextAsync(scriptPath, scriptContent);
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"--noprofile --norc \"{scriptPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = srcDir 
        };
        
        ConfigureBuildEnvironment(psi, sysConfig, manifest, srcDir, absoluteBuildDir, absoluteStartDir);
        
        var logFile = Path.Combine(buildDir, "build.log");
        using var process = Process.Start(psi);
        if (process == null) throw new Exception("Failed to start monolithic build process.");

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
                currentPkgName = trimmed.Split('|', 2)[1];
            }
            else if (trimmed == "---AURORA_METADATA_START---" && currentPkgName != null)
            {
                capturingMetadata = true;
                currentMetadata.Clear();
            }
            else if (trimmed == "---AURORA_METADATA_END---" && currentPkgName != null)
            {
                capturingMetadata = false;
                var baseManifest = CloneManifest(manifest);
                baseManifest.Package.Name = currentPkgName;
                var overrides = PkgInfoParser.Parse(currentMetadata.ToString());
                ApplyOverrides(baseManifest, overrides);
                subManifests[currentPkgName] = baseManifest;
            }
            else if (capturingMetadata)
            {
                currentMetadata.AppendLine(line);
            }
        }
        
        process.OutputDataReceived += (_, args) => HandleOutput(args.Data);
        process.ErrorDataReceived += (_, args) => HandleOutput(args.Data);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new Exception($"Build script failed with exit code {process.ExitCode}. See log for details.");
        }

        AnsiConsole.MarkupLine("\n[bold magenta]Finalizing Artifacts...[/]");
        foreach (var (pkgName, subManifest) in subManifests)
        {
            var subPkgDir = Path.Combine(absoluteBuildDir, "pkg", pkgName);
            await ArtifactCreator.CreateAsync(subManifest, subPkgDir, absoluteStartDir);
        }

        AnsiConsole.MarkupLine("\n[green bold]✔ Build process completed successfully![/]");
    }

    private string GenerateMonolithicScript(AuroraManifest m, MakepkgConfig c, string srcDir, string buildDir, string startDir, bool useFakeroot)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/bin/bash");
        sb.AppendLine("set -e");
        sb.AppendLine("shopt -s nullglob globstar");

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
        sb.AppendLine("for pkg_name_entry in \"${pkgname[@]}\"; do");
        sb.AppendLine("  pkgdir_entry=\"" + buildDir + "/pkg/$pkg_name_entry\"");
        sb.AppendLine("  rm -rf \"$pkgdir_entry\"");
        sb.AppendLine("  mkdir -p \"$pkgdir_entry\"");
        
        // Export variables so the sub-shell (fakeroot) can see them
        sb.AppendLine("  export CURRENT_PKG_NAME=\"$pkg_name_entry\"");
        sb.AppendLine("  export CURRENT_PKG_DIR=\"$pkgdir_entry\"");
        
        sb.AppendLine("  msg \"Packaging $CURRENT_PKG_NAME...\"");
        sb.AppendLine("  echo \"---AURORA_PACKAGE_START|$CURRENT_PKG_NAME\"");
        
        // Sub-shell command for packaging
        string cmd = $"source '{pkgbuild}'; " +
                     "export pkgname=\"$CURRENT_PKG_NAME\"; " +
                     "export pkgdir=\"$CURRENT_PKG_DIR\"; " +
                     $"export srcdir='{srcDir}'; " +
                     $"export startdir='{startDir}'; " +
                     "pkg_func=\"package_$pkgname\"; " +
                     "if ! type -t \"$pkg_func\" &>/dev/null; then pkg_func='package'; fi; " +
                     "cd \"$srcdir\" && \"$pkg_func\"";

        if (useFakeroot) sb.AppendLine($"  fakeroot bash -c '{cmd}'");
        else sb.AppendLine($"  bash -c '{cmd}'");
        
        // Metadata is scraped in the main shell context
        sb.AppendLine("  export pkgname=\"$CURRENT_PKG_NAME\"");
        sb.AppendLine("  export pkgdir=\"$CURRENT_PKG_DIR\"");
        sb.AppendLine("  scrape_metadata");
        sb.AppendLine("done");

        return sb.ToString();
    }
    
    private void ConfigureBuildEnvironment(ProcessStartInfo psi, MakepkgConfig c, AuroraManifest m, string srcDir, string buildDir, string startDir)
    {
        var path = "/usr/local/bin:/usr/bin:/bin:/usr/local/sbin:/usr/sbin:/sbin";
        if (m.Build.Options.Contains("ccache"))
        {
            path = "/usr/lib/ccache/bin:" + path;
        }
        psi.Environment["PATH"] = path;

        psi.Environment["SHELL"] = "/bin/bash";
        psi.Environment["LC_ALL"] = "C";
        psi.Environment["LANG"] = "C";
        psi.Environment["HOME"] = buildDir;

        psi.Environment["srcdir"] = srcDir;
        psi.Environment["startdir"] = startDir;

        psi.Environment["CARCH"] = c.Arch;
        psi.Environment["CHOST"] = c.Chost;

        psi.Environment["CFLAGS"] = c.CFlags;
        psi.Environment["CXXFLAGS"] = c.CxxFlags;
        psi.Environment["CPPFLAGS"] = c.CppFlags;
        psi.Environment["LDFLAGS"] = c.LdFlags;
        psi.Environment["MAKEFLAGS"] = c.MakeFlags;

        if (m.Build.Options.Contains("debug"))
        {
            var map = $"-ffile-prefix-map={srcDir}=/usr/src/debug/{m.Package.Name}";
            psi.Environment["CFLAGS"] += $" {c.DebugCFlags} {map}";
            psi.Environment["CXXFLAGS"] += $" {c.DebugCxxFlags} {map}";
        }

        if (m.Build.Options.Contains("lto"))
        {
            psi.Environment["CFLAGS"] += $" {c.LtoFlags}";
            psi.Environment["CXXFLAGS"] += $" {c.LtoFlags}";
            psi.Environment["LDFLAGS"] += $" {c.LtoFlags}";
        }
        
        psi.Environment["PACKAGER"] = c.Packager;
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