using Aurora.Core.Models;

namespace Aurora.Core.Net.DownloadProviders;

public class HttpProvider : IDownloadProvider
{
    public string[] SupportedProtocols => new[] { "http", "https", "ftp" };
    private static readonly HttpClient _client = new() { Timeout = TimeSpan.FromMinutes(10) };

    public async Task DownloadAsync(SourceEntry entry, string destinationPath, Action<long?, long> onProgress)
    {
        using var response = await _client.GetAsync(entry.Url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        
        await using var downloadStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[8192]; // 8KB chunks
        long totalDownloaded = 0;
        int bytesRead;

        while ((bytesRead = await downloadStream.ReadAsync(buffer, 0, buffer.Length)) != 0)
        {
            await fileStream.WriteAsync(buffer, 0, bytesRead);
            totalDownloaded += bytesRead;
            
            // Report progress back to UI
            onProgress(totalBytes, totalDownloaded);
        }
    }
}