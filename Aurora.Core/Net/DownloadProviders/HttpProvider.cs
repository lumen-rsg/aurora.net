using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Aurora.Core.Models;

namespace Aurora.Core.Net.DownloadProviders;

public class HttpProvider : IDownloadProvider
{
    public string[] SupportedProtocols => new[] { "http", "https", "ftp" };
    
    private static readonly HttpClient _client;

    static HttpProvider()
    {
        // 1. Force IPv4 (GNU mirrors often hang on broken IPv6)
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = async (context, cancellationToken) =>
            {
                // Force IPv4 addresses only
                var entry = await Dns.GetHostEntryAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, cancellationToken);
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(new IPEndPoint(entry.AddressList[0], context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, true);
            }
        };

        _client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(30)
        };
        
        // 2. Full Browser Header Suite
        _client.DefaultRequestHeaders.Clear();
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36");
        _client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
        _client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        _client.DefaultRequestHeaders.Add("Sec-Ch-Ua", "\"Not A(Brand\";v=\"99\", \"Google Chrome\";v=\"121\", \"Chromium\";v=\"121\"");
        _client.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "document");
        _client.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "navigate");
        _client.DefaultRequestHeaders.Add("Connection", "keep-alive");
    }

    public async Task DownloadAsync(SourceEntry entry, string destinationPath, Action<long?, long> onProgress)
    {
        // Use a "Referer" to look like we clicked a link from the GNU index
        var request = new HttpRequestMessage(HttpMethod.Get, entry.Url);
        request.Headers.Referrer = new Uri(entry.Url.Substring(0, entry.Url.LastIndexOf('/') + 1));

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Mirror error: {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var totalBytes = response.Content.Headers.ContentLength;
        
        await using var downloadStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[32768]; // 32KB buffer for faster writing
        long totalDownloaded = 0;
        int bytesRead;

        while ((bytesRead = await downloadStream.ReadAsync(buffer)) != 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
            totalDownloaded += bytesRead;
            onProgress(totalBytes, totalDownloaded);
        }
    }
}