using System.Diagnostics;
using Aurora.Core.Logging;
using Spectre.Console;

namespace Aurora.Core.Logic;

public static class ScriptRunner
{
    public static void RunScript(string scriptPath, string functionName, string sysRoot, string version, string? oldVersion = null)
    {
        if (!File.Exists(scriptPath)) return;

        // Arch .INSTALL standard:
        // post_install <package_version>
        // post_upgrade <new_version> <old_version>
        // pre_remove <old_version>
        string args = $"'{version}'";
        if (!string.IsNullOrEmpty(oldVersion)) args += $" '{oldVersion}'";

        // Shim to source the file and run the function if it exists
        string bashCommand = $"source '{scriptPath}'; if type -t {functionName} | grep -q 'function'; then {functionName} {args}; fi";

        // Logic for Chroot vs Host
        ProcessStartInfo psi;
        bool isChroot = sysRoot != "/";

        if (isChroot)
        {
            // When bootstrapping, scripts must run inside the target
            // But the script file is likely on the host at this exact moment?
            // Actually, in 'InstallCommand', we extract .INSTALL to a temp file.
            // We need to ensure that temp file is accessible to the chroot.
            // For safety/simplicity in bootstrapping, usually .INSTALL scripts are run 
            // via 'arch-chroot' or equivalent.
            
            // To keep it simple: We map the script path to inside the root if possible,
            // or we pipe the script content.
            
            // For now, let's assume we copy the script into the chroot /tmp to run it.
            string chrootScriptPath = Path.Combine(sysRoot, "tmp", Path.GetFileName(scriptPath));
            File.Copy(scriptPath, chrootScriptPath, true);
            string innerPath = "/tmp/" + Path.GetFileName(scriptPath);
            
            string innerCommand = $"source '{innerPath}'; if type -t {functionName} | grep -q 'function'; then {functionName} {args}; fi";
            
            psi = new ProcessStartInfo
            {
                FileName = "chroot",
                Arguments = $"\"{sysRoot}\" /bin/bash -c \"{innerCommand}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }
        else
        {
            psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{bashCommand}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }

        AnsiConsole.MarkupLine($"[grey]Running scriptlet: {functionName} {version}...[/]");

        using var process = Process.Start(psi);
        if (process == null) return;

        // Stream Output Live
        process.OutputDataReceived += (s, e) => { if (e.Data != null) PrintScriptlet(e.Data, false); };
        process.ErrorDataReceived += (s, e) => { if (e.Data != null) PrintScriptlet(e.Data, true); };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();

        // Cleanup temp file in chroot
        if (isChroot)
        {
            try { File.Delete(Path.Combine(sysRoot, "tmp", Path.GetFileName(scriptPath))); } catch {}
        }

        if (process.ExitCode != 0)
        {
            AnsiConsole.MarkupLine($"[yellow]Scriptlet '{functionName}' returned non-zero exit code {process.ExitCode}.[/]");
        }
    }

    private static void PrintScriptlet(string line, bool isError)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        string prefix = isError ? "[red]err:|[/]" : "[grey]out:|[/]";
        AnsiConsole.MarkupLine($"    {prefix} {Markup.Escape(line)}");
    }
}