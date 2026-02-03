using System.Diagnostics;
using System.Text.RegularExpressions;
using Aurora.Core.Contract;
using Spectre.Console;

namespace Aurora.Core.Logic.Build;

public static class ElfLinker
{
    // Captures: Library soname: [libreadline.so.8]
    private static readonly Regex SonameRegex = new(@"Library soname: \[(.*?)\]", RegexOptions.Compiled);
    
    // Captures: Shared library: [libc.so.6]
    private static readonly Regex NeededRegex = new(@"Shared library: \[(.*?)\]", RegexOptions.Compiled);
    
    // Captures: Class: ELF64
    private static readonly Regex ElfClassRegex = new(@"Class:\s+(ELF\d+)", RegexOptions.Compiled);

    // Helper regex to split "libname.so.1.2" into "libname.so" and "1.2"
    // This ensures we normalize versioning.
    private static readonly Regex LibVersionSplitter = new(@"^(.*?\.so)(?:\.(.*))?$", RegexOptions.Compiled);

    public static async Task ProcessArtifactsAsync(AuroraManifest manifest, string pkgDir)
    {
        var providedLibs = new HashSet<string>(); // SONAMEs explicitly provided by this package
        var neededLibs = new HashSet<string>();   // SONAMEs needed by binaries in this package

        // 1. Find all ELF files (Shared Objects and Executables)
        // We scan everything executable or .so to find dependencies
        var files = Directory.GetFiles(pkgDir, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).StartsWith(".")); 

        foreach (var file in files)
        {
            var fileInfo = new FileInfo(file);
            if (fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

            // Simple optimization: Skip obvious non-binaries (txt, html, etc) to save readelf calls
            // In a real scenario, we might read the first 4 bytes for the ELF magic header (7F 45 4C 46)
            if (!IsPotentialElf(file)) continue;

            var result = await AnalyzeElfAsync(file);
            if (result.Arch == null) continue; // Not an ELF file

            // A. Auto-Provides (Only for .so files)
            if (!string.IsNullOrEmpty(result.Soname))
            {
                var formatted = FormatCapability(result.Soname, result.Arch);
                if (formatted != null) providedLibs.Add(formatted);
                
                // Track the raw SONAME to filter self-references later
                // e.g., if I provide libinternal.so, I shouldn't depend on it.
                // Note: We need the raw name "libinternal.so.1" for this check.
                // We add the formatted version to the manifest later.
            }

            // B. Auto-Depends (For executables AND .so files)
            foreach (var need in result.Needed)
            {
                var formatted = FormatCapability(need, result.Arch);
                if (formatted != null) neededLibs.Add(formatted);
            }
        }

        // 2. Apply Provides
        if (providedLibs.Count > 0)
        {
            AnsiConsole.MarkupLine($"[grey]  -> Detected {providedLibs.Count} library provisions[/]");
            foreach (var prov in providedLibs)
            {
                if (!manifest.Metadata.Provides.Contains(prov))
                    manifest.Metadata.Provides.Add(prov);
            }
        }

        // 3. Apply Depends (Filtering out self-provided libs)
        // We iterate the RAW needed strings we formatted.
        // We must check if the formatted string is inside our OWN provided list.
        int addedDeps = 0;
        foreach (var need in neededLibs)
        {
            // Do not depend on something we provide ourselves
            if (providedLibs.Contains(need)) continue;

            // Do not duplicate existing manual dependencies
            // (Note: manual depends are usually package names like 'glibc', 
            // these are capabilities 'libc.so=6-64'. We keep BOTH.
            // This ensures strict version safety.)
            if (!manifest.Dependencies.Runtime.Contains(need))
            {
                manifest.Dependencies.Runtime.Add(need);
                addedDeps++;
            }
        }
        
        if (addedDeps > 0)
        {
            AnsiConsole.MarkupLine($"[grey]  -> Detected {addedDeps} runtime links (NEEDED)[/]");
        }
    }

    private static bool IsPotentialElf(string path)
    {
        // Quick header check
        try 
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < 4) return false;
            var bytes = new byte[4];
            fs.Read(bytes, 0, 4);
            // ELF Magic Number: 0x7F 'E' 'L' 'F'
            return bytes[0] == 0x7F && bytes[1] == 0x45 && bytes[2] == 0x4C && bytes[3] == 0x46;
        }
        catch { return false; }
    }

    private static string? FormatCapability(string libName, string elfClass)
    {
        // ELF64 -> 64, ELF32 -> 32
        var archSuffix = elfClass == "ELF64" ? "64" : "32";

        // Logic: libreadline.so.8.2 -> Name: libreadline.so, Ver: 8.2
        // Logic: libc.so.6          -> Name: libc.so, Ver: 6
        var match = LibVersionSplitter.Match(libName);

        if (match.Success)
        {
            var baseName = match.Groups[1].Value;
            var ver = match.Groups[2].Value;

            if (string.IsNullOrEmpty(ver))
            {
                // Unversioned libs (e.g. plugin.so)
                // We typically exclude these from strict dependencies unless policy dictates otherwise
                // For now, let's include them as unversioned capabilities.
                return $"{baseName}-{archSuffix}";
            }

            return $"{baseName}={ver}-{archSuffix}";
        }
        return null;
    }

    private class ElfInfo
    {
        public string? Soname { get; set; }
        public string? Arch { get; set; }
        public List<string> Needed { get; set; } = new();
    }

    private static async Task<ElfInfo> AnalyzeElfAsync(string filePath)
    {
        var info = new ElfInfo();
        try
        {
            // -d: Dynamic section (NEEDED, SONAME)
            // -h: File Header (Class/Arch)
            var psi = new ProcessStartInfo
            {
                FileName = "readelf",
                Arguments = $"-d -h \"{filePath}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi);
            if (p == null) return info;

            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0) return info;

            // Parse Arch
            var classMatch = ElfClassRegex.Match(output);
            if (classMatch.Success) info.Arch = classMatch.Groups[1].Value;
            else return info; // Not a valid ELF we care about

            // Parse SONAME
            var sonameMatch = SonameRegex.Match(output);
            if (sonameMatch.Success) info.Soname = sonameMatch.Groups[1].Value;

            // Parse NEEDED (Multiple entries)
            var needMatches = NeededRegex.Matches(output);
            foreach (Match m in needMatches)
            {
                info.Needed.Add(m.Groups[1].Value);
            }

            return info;
        }
        catch
        {
            return info;
        }
    }
}