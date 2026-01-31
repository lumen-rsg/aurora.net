using Aurora.Core.Models;

namespace Aurora.Core.Net.DownloadProviders;

public class LocalProvider : IDownloadProvider
{
    public string[] SupportedProtocols => new[] { "local" };

    public Task DownloadAsync(SourceEntry entry, string destinationPath, Action<string> onProgress)
    {
        // For local files, we assume they are already in the startdir.
        // We don't need to 'download' them, but we verify they exist.
        if (!File.Exists(destinationPath))
        {
            throw new FileNotFoundException($"Local source file not found: {entry.FileName}");
        }
        onProgress("Local file verified.");
        return Task.CompletedTask;
    }
}