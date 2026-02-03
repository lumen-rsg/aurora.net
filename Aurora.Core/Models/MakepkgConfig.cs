namespace Aurora.Core.Models;

public class MakepkgConfig
{
    public string Arch { get; set; } = "x86_64";
    public string Chost { get; set; } = "x86_64-pc-linux-gnu";

    // Standard Arch Linux Compiler Flags
    public string CFlags { get; set; } = "-march=x86-64 -mtune=generic -O2 -pipe -fno-plt -fexceptions " +
                                         "-Wp,-D_FORTIFY_SOURCE=3 -Wformat -Werror=format-security " +
                                         "-fstack-clash-protection -fcf-protection " +
                                         "-fno-omit-frame-pointer -mno-omit-leaf-frame-pointer";

    public string CxxFlags { get; set; } = "-march=x86-64 -mtune=generic -O2 -pipe -fno-plt -fexceptions " +
                                           "-Wp,-D_FORTIFY_SOURCE=3 -Wformat -Werror=format-security " +
                                           "-fstack-clash-protection -fcf-protection " +
                                           "-fno-omit-frame-pointer -mno-omit-leaf-frame-pointer " +
                                           "-Wp,-D_GLIBCXX_ASSERTIONS";

    public string CppFlags { get; set; } = ""; // Usually empty in modern Arch

    public string LdFlags { get; set; } = "-Wl,-O1 -Wl,--sort-common -Wl,--as-needed -Wl,-z,relro -Wl,-z,now " +
                                          "-Wl,-z,pack-relative-relocs";

    public string LtoFlags { get; set; } = "-flto=auto";
    
    public string MakeFlags { get; set; } = "-j" + Environment.ProcessorCount;

    public string DebugCFlags { get; set; } = "-g";
    public string DebugCxxFlags { get; set; } = "-g";

    public string Packager { get; set; } = "Aurora Build System <aurora@lumina-distro.org>";
}