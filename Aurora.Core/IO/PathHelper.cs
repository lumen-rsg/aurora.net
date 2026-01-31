namespace Aurora.Core.IO;

public static class PathHelper
{
    /// <summary>
    /// Combines a root path with a system absolute path safely.
    /// Example: Combine("/mnt/root", "/usr/bin/bash") -> "/mnt/root/usr/bin/bash"
    /// </summary>
    public static string GetPath(string root, string systemPath)
    {
        // Remove leading slash to ensure Path.Combine treats it as relative
        var relative = systemPath.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(root, relative));
    }
}