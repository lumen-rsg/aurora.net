using System.Text;

namespace Aurora.Core.Logging;

public static class AuLogger
{
    private static readonly object _lock = new();
    private static string _logPath = "aurora.log";

    public static void Initialize(string path)
    {
        _logPath = path;
        // Reset log on new run or append? Usually append with timestamp.
        File.AppendAllText(_logPath, $"\n--- Session Start: {DateTime.Now} ---\n");
    }

    public static void Log(string level, string message)
    {
        lock (_lock)
        {
            var entry = $"[{DateTime.Now:HH:mm:ss}] [{level.ToUpper()}] {message}\n";
            File.AppendAllText(_logPath, entry, Encoding.UTF8);
        }
    }

    public static void Info(string msg) => Log("INFO", msg);
    public static void Error(string msg) => Log("ERROR", msg);
    public static void Debug(string msg) => Log("DEBUG", msg);
}