using Aurora.Core.Models;
using Aurora.Core.Net;
using Aurora.Core.Net.DownloadProviders;

namespace Aurora.Core.Logic;

public class SourceManager
{
    private readonly List<IDownloadProvider> _providers = new();

    public SourceManager()
    {
        _providers.Add(new LocalProvider());
        _providers.Add(new HttpProvider());
        _providers.Add(new GitProvider()); // <--- Registered
    }

    public async Task FetchSourceAsync(SourceEntry entry, string srcDest, Action<string> onProgress)
    {
        var provider = _providers.FirstOrDefault(p => 
            p.SupportedProtocols.Contains(entry.Protocol));

        if (provider == null)
            throw new NotSupportedException($"No download provider found for protocol: {entry.Protocol}");

        var destPath = Path.Combine(srcDest, entry.FileName);
        
        await provider.DownloadAsync(entry, destPath, onProgress);
    }
}