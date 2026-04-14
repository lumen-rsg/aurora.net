using System.Text;

namespace Aurora.Core.Logging;

public static class AuLogger
{
    private static readonly object _lock = new();
    private static string _logPath = "/var/log/aurora/aurora.log";
    private static bool _silent;

    /// <summary>
    ///     Initializes the logger with a deterministic log path.
    ///     If no path is provided, defaults to /var/log/aurora/aurora.log.
    ///     If the log directory cannot be created, logging degrades gracefully (silent mode).
    /// </summary>
    public static void Initialize(string? sysRoot = null)
    {
        _silent = false;

        if (!string.IsNullOrEmpty(sysRoot) && sysRoot != "/")
        {
            // Bootstrapped environment: logs go inside the sysroot
            var logDir = Path.Combine(sysRoot, "var/log/aurora");
            try { Directory.CreateDirectory(logDir); } catch { _silent = true; }
            _logPath = Path.Combine(logDir, "aurora.log");
        }
        else
        {
            // Native Linux: use standard /var/log/aurora/
            try { Directory.CreateDirectory("/var/log/aurora"); } catch { }
            _logPath = "/var/log/aurora/aurora.log";
        }

        try
        {
            File.AppendAllText(_logPath, $"\n--- Session Start: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---\n",
                Encoding.UTF8);
        }
        catch
        {
            // Cannot write to log file (e.g. no permissions) — degrade silently
            _silent = true;
        }
    }

    public static void Log(string level, string message)
    {
        if (_silent) return;

        lock (_lock)
        {
            try
            {
                var entry = $"[{DateTime.Now:HH:mm:ss}] [{level.ToUpper()}] {message}\n";
                File.AppendAllText(_logPath, entry, Encoding.UTF8);
            }
            catch
            {
                // Non-critical: logging should never crash the application
            }
        }
    }

    public static void Info(string msg) => Log("INFO", msg);
    public static void Error(string msg) => Log("ERROR", msg);
    public static void Debug(string msg) => Log("DEBUG", msg);
    public static void Warn(string msg) => Log("WARN", msg);
}
