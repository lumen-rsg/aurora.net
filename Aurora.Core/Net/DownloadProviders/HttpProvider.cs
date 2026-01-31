using Aurora.Core.Models;

namespace Aurora.Core.Net.DownloadProviders;

public class HttpProvider : IDownloadProvider
{
    public string[] SupportedProtocols => new[] { "http", "https", "ftp" };
    private static readonly HttpClient _client = new() { Timeout = TimeSpan.FromMinutes(10) };

    public async Task DownloadAsync(SourceEntry entry, string destinationPath, Action<string> onProgress)
    {
        onProgress($"Downloading {entry.FileName}...");
        
        using var response = await _client.GetAsync(entry.Url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await response.Content.CopyToAsync(fs);
    }
}