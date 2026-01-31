using System.Runtime.InteropServices;

namespace Aurora.Core.IO;

internal static partial class Syscall
{
    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int lchown(string path, uint owner, uint group);
    
    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int lsetxattr(string path, string name, byte[] value, ulong size, int flags);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int symlink(string target, string linkpath);
}