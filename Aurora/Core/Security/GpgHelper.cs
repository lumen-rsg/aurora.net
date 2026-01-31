using System.Diagnostics;
using Aurora.Core.Logging;

namespace Aurora.Core.Security;

public static class GpgHelper
{
    /// <summary>
    /// Verifies a detached signature.
    /// Returns true if the signature is valid and trusted.
    /// </summary>
    public static bool VerifySignature(string dataFile, string sigFile, string homeDir = null)
    {
        if (!File.Exists(dataFile)) throw new FileNotFoundException(dataFile);
        if (!File.Exists(sigFile)) throw new FileNotFoundException(sigFile);

        var psi = new ProcessStartInfo
        {
            FileName = "gpg",
            // --status-fd 1: outputs machine readable status to stdout
            // --verify: verify the signature
            Arguments = "--status-fd 1 --verify " + 
                        $"\"{sigFile}\" \"{dataFile}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrEmpty(homeDir))
        {
            // For testing, we point to a custom keychain
            psi.EnvironmentVariables["GNUPGHOME"] = homeDir;
        }

        try
        {
            using var process = Process.Start(psi);
            if (process == null) return false;

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            // GPG returns 0 for a good signature (usually).
            // However, we should check the status output for [GNUPG:] GOODSIG
            // or VALIDSIG to be absolutely sure.
            
            bool isGood = output.Contains("[GNUPG:] GOODSIG") || output.Contains("[GNUPG:] VALIDSIG");

            if (!isGood)
            {
                AuLogger.Error($"GPG Verification Failed for {Path.GetFileName(dataFile)}");
                AuLogger.Debug($"GPG Output: {output}");
                AuLogger.Debug($"GPG Error: {error}");
            }
            else
            {
                AuLogger.Debug($"Signature valid for {Path.GetFileName(dataFile)}");
            }

            return isGood;
        }
        catch (Exception ex)
        {
            AuLogger.Error($"Failed to run GPG: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Signs a file (Detached signature).
    /// Used by the repo generator.
    /// </summary>
    public static void SignFile(string filePath, string homeDir = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "gpg",
            // -a: Armor (ASCII)
            // -b: Detach sign
            // --yes: Overwrite existing
            Arguments = $"-a -b --yes \"{filePath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrEmpty(homeDir))
        {
            psi.EnvironmentVariables["GNUPGHOME"] = homeDir;
        }

        using var process = Process.Start(psi);
        process?.WaitForExit();
        
        if (process?.ExitCode != 0)
        {
            throw new Exception("GPG Signing failed.");
        }
    }
}