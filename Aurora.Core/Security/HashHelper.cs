using System.Security.Cryptography;

namespace Aurora.Core.Security;

public static class HashHelper
{
    public static string ComputeFileHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(stream);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}