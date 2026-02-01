using System.Diagnostics;

namespace Aurora.Core.Logic.Build;

public static class EditorHelper
{
    public static void OpenFileInEditor(string filePath)
    {
        // 1. Determine the editor
        string editor = Environment.GetEnvironmentVariable("EDITOR");
        if (string.IsNullOrEmpty(editor))
        {
        // Fallback to common defaults if $EDITOR is not set
            editor = "nano"; // or "vi", "vim"
        }

        // 2. Launch the process
        // We do NOT redirect I/O here, so the user can interact with the editor
        // in their current terminal session.
        var psi = new ProcessStartInfo(editor, $"\"{filePath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(psi);
            process?.WaitForExit();
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to launch editor '{editor}'. Is it in your PATH? Error: {ex.Message}");
        }
    }
}