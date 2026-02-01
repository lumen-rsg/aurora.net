using System.Runtime.InteropServices;

namespace Aurora.Core.IO;

public static partial class Syscall
{
    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial int lchown(string path, uint owner, uint group);
    
    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial int lsetxattr(string path, string name, byte[] value, ulong size, int flags);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial int symlink(string target, string linkpath);
    
    [LibraryImport("libc", SetLastError = true)]
    public static partial uint geteuid();
}