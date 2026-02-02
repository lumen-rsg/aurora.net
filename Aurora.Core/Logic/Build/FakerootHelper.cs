using System.Diagnostics;
using Spectre.Console;

namespace Aurora.Core.Logic.Build;

public static class FakerootHelper
{
    private static bool? _isAvailable;

    public static bool IsAvailable()
    {
        if (_isAvailable.HasValue) return _isAvailable.Value;

        try
        {
            using var proc = Process.Start(new ProcessStartInfo {
                FileName = "fakeroot", Arguments = "-v",
                RedirectStandardOutput = true, UseShellExecute = false
            });
            proc.WaitForExit();
            _isAvailable = proc.ExitCode == 0;
        }
        catch
        {
            _isAvailable = false;
        }
        return _isAvailable.Value;
    }

    public static ProcessStartInfo WrapInFakeroot(ProcessStartInfo originalPsi)
    {
        if (!IsAvailable())
        {
            AnsiConsole.MarkupLine("[bold yellow]Warning:[/] 'fakeroot' binary not found. Package file ownership will be incorrect.");
            return originalPsi;
        }

        var fakerootPsi = new ProcessStartInfo
        {
            FileName = "fakeroot",
            // Pass -- to signal end of fakeroot args
            Arguments = $"-- \"{originalPsi.FileName}\" {originalPsi.Arguments}",
            
            // FIX: Ensure all redirections are copied from the original PSI
            RedirectStandardInput = originalPsi.RedirectStandardInput, 
            RedirectStandardOutput = originalPsi.RedirectStandardOutput,
            RedirectStandardError = originalPsi.RedirectStandardError,
            
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = originalPsi.WorkingDirectory
        };
        
        // Copy environment variables
        foreach (var (key, value) in originalPsi.Environment)
        {
            fakerootPsi.Environment[key] = value;
        }

        return fakerootPsi;
    }
}