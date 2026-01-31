using Aurora.Core.Models;

namespace Aurora.Core.Net.DownloadProviders;

public class LocalProvider : IDownloadProvider
{
    private readonly string _projectDir;

    public string[] SupportedProtocols => new[] { "local" };

    // Pass the project directory (startdir) to the provider
    public LocalProvider(string projectDir)
    {
        _projectDir = projectDir;
    }

    public Task DownloadAsync(SourceEntry entry, string destinationPath, Action<string> onProgress)
    {
        // For local files, the "source" is the project directory
        string localPath = Path.Combine(_projectDir, entry.FileName);

        if (!File.Exists(localPath))
        {
            throw new FileNotFoundException($"Local source file not found: {entry.FileName}");
        }

        onProgress("Verified local source.");
        return Task.CompletedTask;
    }
}