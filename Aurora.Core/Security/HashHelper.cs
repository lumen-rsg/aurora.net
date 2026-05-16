using System.Security.Cryptography;

namespace Aurora.Core.Security;

public static class HashHelper
{
    public static string ComputeHash(string filePath, string algorithm = "sha256")
    {
        using var stream = File.OpenRead(filePath);
        using var hashAlg = CreateAlgorithm(algorithm);
        var bytes = hashAlg.ComputeHash(stream);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static bool VerifyFile(string filePath, string expectedHash, string algorithm = "sha256")
    {
        var actual = ComputeHash(filePath, algorithm);
        return actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    // Backward compat
    public static string ComputeFileHash(string filePath) => ComputeHash(filePath, "sha256");

    private static HashAlgorithm CreateAlgorithm(string algorithm) => algorithm.ToLowerInvariant() switch
    {
        "sha" or "sha1" => SHA1.Create(),
        "sha256" => SHA256.Create(),
        "sha512" => SHA512.Create(),
        _ => throw new ArgumentException($"Unsupported hash algorithm: {algorithm}", nameof(algorithm))
    };
}
