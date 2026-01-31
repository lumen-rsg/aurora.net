using System.Diagnostics;
using Aurora.Core.Logging;

namespace Aurora.Core.Logic;

public static class ScriptRunner
{
    public static void RunScript(string scriptPath, string functionName, string rootPath)
    {
        if (!File.Exists(scriptPath)) return;

        // We use /bin/bash explicitly
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // The Magic Command:
        // 1. Source the file.
        // 2. Check if function exists.
        // 3. Execute it with rootPath as argument.
        var escapedPath = scriptPath.Replace("'", "'\\''");
        var escapedRoot = rootPath.Replace("'", "'\\''");
        
        psi.Arguments = $"-c \"source '{escapedPath}'; if type -t {functionName} | grep -q 'function'; then {functionName} '{escapedRoot}'; fi\"";

        AuLogger.Debug($"Executing script hook: {functionName}");

        try
        {
            using var process = Process.Start(psi);
            if (process == null) return;

            // Read output to log it
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();

            process.WaitForExit();

            if (!string.IsNullOrWhiteSpace(stdout)) AuLogger.Info($"[Script {functionName}] {stdout.Trim()}");
            
            if (process.ExitCode != 0)
            {
                // Warning only, as requested
                AuLogger.Error($"[Script Warning] Hook '{functionName}' failed (Exit Code {process.ExitCode}).");
                if (!string.IsNullOrWhiteSpace(stderr)) AuLogger.Error($"Stderr: {stderr.Trim()}");
            }
        }
        catch (Exception ex)
        {
            AuLogger.Error($"Failed to run script runner: {ex.Message}");
        }
    }
}