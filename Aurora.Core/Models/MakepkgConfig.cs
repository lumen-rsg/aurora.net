namespace Aurora.Core.Models;

public class MakepkgConfig
{
    // Architecture
    public string Arch { get; set; } = "x86_64";
    public string Chost { get; set; } = string.Empty; // CRITICAL: e.g. x86_64-pc-linux-gnu

    // Compiler Flags
    public string CFlags { get; set; } = "-march=x86-64 -mtune=generic -O2 -pipe -fno-plt";
    public string CxxFlags { get; set; } = "-march=x86-64 -mtune=generic -O2 -pipe -fno-plt";
    public string CppFlags { get; set; } = "-D_FORTIFY_SOURCE=2"; // Preprocessor flags
    public string LdFlags { get; set; } = "-Wl,-O1,--sort-common,--as-needed,-z,relro,-z,now";
    public string LtoFlags { get; set; } = "-flto=auto";
    public string MakeFlags { get; set; } = "-j$(nproc)";
    
    // Debug Flags
    public string DebugCFlags { get; set; } = "-g -fvar-tracking-assignments";
    public string DebugCxxFlags { get; set; } = "-g -fvar-tracking-assignments";
    public string DebugRustFlags { get; set; } = "-C debuginfo=2";

    // Identity
    public string Packager { get; set; } = "Aurora Build System <aurora@localhost>";
    
    // Options array is tricky to map to a class, but we parses specific flags above
    // We could store the raw 'OPTIONS' array if we implemented logic to parse it,
    // but usually CFLAGS/CHOST are what breaks builds.
}