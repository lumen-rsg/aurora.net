using System.Diagnostics;
using Aurora.Core.Logging;
using Spectre.Console;

namespace Aurora.Core.Logic;

public static class ScriptRunner
{
    public static void RunScript(string scriptPath, string functionName, string sysRoot, string version, string? oldVersion = null)
    {
        if (!File.Exists(scriptPath)) return;

        // Arch .INSTALL standard args
        string args = $"'{version}'";
        if (!string.IsNullOrEmpty(oldVersion)) args += $" '{oldVersion}'";

        string bashCommand = $"source '{scriptPath}'; if type -t {functionName} | grep -q 'function'; then {functionName} {args}; fi";

        ProcessStartInfo psi;
        bool isChroot = sysRoot != "/";

        if (isChroot)
        {
            // BOOTSTRAP MODE
            // We copy the script into the chroot to ensure visibility
            string tmpDir = Path.Combine(sysRoot, "tmp");
            Directory.CreateDirectory(tmpDir); // Ensure tmp exists
            
            string chrootScriptPath = Path.Combine(tmpDir, Path.GetFileName(scriptPath));
            File.Copy(scriptPath, chrootScriptPath, true);
            
            string innerPath = "/tmp/" + Path.GetFileName(scriptPath);
            
            // CRITICAL FIX: explicit 'cd /' to ensure relative paths in scripts work
            string innerCommand = $"cd /; source '{innerPath}'; if type -t {functionName} | grep -q 'function'; then {functionName} {args}; fi";
            
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
            // HOST MODE
            psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{bashCommand}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                // CRITICAL FIX: Set working directory to SysRoot (usually /)
                WorkingDirectory = sysRoot 
            };
        }

        AnsiConsole.MarkupLine($"[grey]Running scriptlet: {functionName} {version}...[/]");

        using var process = Process.Start(psi);
        if (process == null) return;

        process.OutputDataReceived += (s, e) => { if (e.Data != null) PrintScriptlet(e.Data, false); };
        process.ErrorDataReceived += (s, e) => { if (e.Data != null) PrintScriptlet(e.Data, true); };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();

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
        // Escape markup to prevent crashing on brackets in output
        AnsiConsole.MarkupLine($"    {prefix} {Markup.Escape(line)}");
    }
}