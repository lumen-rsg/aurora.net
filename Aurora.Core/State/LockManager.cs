using Aurora.Core.Logging;

namespace Aurora.Core.State;

public class LockManager : IDisposable
{
    private readonly string _lockPath;
    private FileStream? _lockStream;

    public LockManager(string lockPath = "aurora.lock")
    {
        _lockPath = lockPath;
    }

    public void Acquire()
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_lockPath));
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir!);

            // FileShare.None is the key: It requests exclusive access.
            // If another process has this open, this line throws IOException.
            _lockStream = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            
            // Write PID for debugging info
            _lockStream.SetLength(0);
            using var writer = new StreamWriter(_lockStream, leaveOpen: true);
            writer.Write(Environment.ProcessId);
            writer.Flush();
            
            AuLogger.Debug($"Acquired system lock: {_lockPath}");
        }
        catch (IOException)
        {
            throw new Exception($"Could not acquire lock on '{_lockPath}'. Is another instance of Aurora running?");
        }
    }

    public void Dispose()
    {
        if (_lockStream != null)
        {
            _lockStream.Close();
            _lockStream.Dispose();
            
            // Optional: delete lock file, or leave it (standard linux behavior is often to leave it)
            try { File.Delete(_lockPath); } catch { }
            
            AuLogger.Debug("Released system lock.");
        }
    }
}