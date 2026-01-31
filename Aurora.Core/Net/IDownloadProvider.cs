using Aurora.Core.Models;

namespace Aurora.Core.Net;

public interface IDownloadProvider
{
    // Protocols this provider handles (e.g., "http", "https")
    string[] SupportedProtocols { get; }

    Task DownloadAsync(SourceEntry entry, string destinationPath, Action<string> onProgress);
}