using Aurora.Core.Models;

namespace Aurora.Core.Net.DownloadProviders;

public class LocalProvider : IDownloadProvider
{
    private readonly string _projectDir;

    public string[] SupportedProtocols => new[] { "local" };

    public LocalProvider(string projectDir)
    {
        _projectDir = projectDir;
    }

    public Task DownloadAsync(SourceEntry entry, string destinationPath, Action<long?, long> onProgress)
    {
        string localPath = Path.Combine(_projectDir, entry.FileName);

        if (!File.Exists(localPath))
        {
            throw new FileNotFoundException($"Local source file not found: {entry.FileName}");
        }

        // Get actual file size to show a full progress bar
        var info = new FileInfo(localPath);
        onProgress(info.Length, info.Length); 
        
        return Task.CompletedTask;
    }
}