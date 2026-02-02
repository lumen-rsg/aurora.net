namespace Aurora.Core.Models;

public class MakepkgConfig
{
    public string CFlags { get; set; } = "-O2 -pipe";
    public string CxxFlags { get; set; } = "-O2 -pipe";
    public string LdFlags { get; set; } = "-Wl,-O1,--sort-common";
    public string MakeFlags { get; set; } = "-j" + Environment.ProcessorCount;
    public string DebugCFlags { get; set; } = "-g";
    public string DebugCxxFlags { get; set; } = "-g";
    public string Arch { get; set; } = "x86_64";
    public string Packager { get; set; } = "Unknown Packager";
}