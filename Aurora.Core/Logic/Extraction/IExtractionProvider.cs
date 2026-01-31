using Aurora.Core.Models;

namespace Aurora.Core.Logic.Extraction;

public interface IExtractionProvider
{
    // Returns true if this provider can handle this specific source
    bool CanHandle(SourceEntry entry);

    // Moves/Unpacks the source from the download cache into the build srcdir
    Task ExtractAsync(SourceEntry entry, string cachePath, string srcDir, Action<string> onProgress);
}