using Aurora.Core.Models;
using Aurora.Core.Net;
using Aurora.Core.Net.DownloadProviders;

namespace Aurora.Core.Logic;

public class SourceManager
{
    private readonly List<IDownloadProvider> _providers = new();

    public SourceManager(string projectDir) // Accept projectDir
    {
        _providers.Add(new LocalProvider(projectDir)); // Pass to LocalProvider
        _providers.Add(new HttpProvider());
        _providers.Add(new GitProvider());
        _providers.Add(new HgProvider()); 
    }

    public async Task FetchSourceAsync(SourceEntry entry, string srcDest, Action<long?, long> onProgress)
    {
        var provider = _providers.FirstOrDefault(p => 
            p.SupportedProtocols.Contains(entry.Protocol));

        if (provider == null)
            throw new NotSupportedException($"No download provider found for protocol: {entry.Protocol}");

        // For non-local protocols, we download into the cache (SRCDEST)
        // For local protocol, the provider just verifies it in the project dir
        var destPath = Path.Combine(srcDest, entry.FileName);
        
        await provider.DownloadAsync(entry, destPath, onProgress);
    }
}