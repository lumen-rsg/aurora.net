namespace Aurora.Core.Models;

public class SourceEntry
{
    public string OriginalString { get; }
    public string FileName { get; }
    public string Url { get; }
    public string Protocol { get; }
    public string? Fragment { get; } // e.g. branch or commit hash
    public bool IsSigned { get; } // NEW

    public SourceEntry(string entry)
    {
        OriginalString = entry;

        // 1. Handle custom filename (filename::url)
        if (entry.Contains("::"))
        {
            var parts = entry.Split("::", 2);
            FileName = parts[0];
            Url = parts[1];
        }
        else
        {
            Url = entry;
            FileName = ParseFileNameFromUrl(Url);
        }

        // 2. Extract Protocol
        Protocol = ExtractProtocol(Url);

        // 3. Extract Fragment (VCS revision)
        if (Url.Contains('#'))
        {
            var fragmentPart = Url.Split('#', 2)[1];
            Fragment = fragmentPart.Split('?')[0];
        }
        
        if (Url.Contains('?'))
        {
            var queryPart = Url.Split('?', 2)[1];
            if (queryPart.Split('#')[0].Split('&').Contains("signed"))
            {
                IsSigned = true;
            }
        }
    }

    private string ExtractProtocol(string url)
    {
        // Explicit protocol separator
        if (url.Contains("://"))
        {
            var proto = url.Split("://")[0];
            
            // Handle composite protocols (git+https -> git)
            if (proto.Contains('+')) return proto.Split('+')[0];
            
            return proto;
        }
        
        // Launchpad bzr specific
        if (url.Contains("lp:")) return "bzr";
        
        // No protocol found -> Local file
        return "local";
    }

    private string ParseFileNameFromUrl(string url)
    {
        // Strip fragment and query
        var cleanUrl = url.Split('#')[0].Split('?')[0].TrimEnd('/');
        var proto = ExtractProtocol(url);
        var baseName = Path.GetFileName(cleanUrl);

        return proto switch
        {
            "git" => baseName.EndsWith(".git") ? baseName[..^4] : baseName,
            "fossil" => baseName + ".fossil",
            _ => baseName
        };
    }
}