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
    
    /// <summary>
    /// Attempts to find the data file corresponding to a signature file.
    /// E.g. input "file.tar.gz.sig" -> returns "file.tar.gz" if it exists.
    /// </summary>
    public static string? FindDataFileForSignature(string signaturePath)
    {
        // Common extensions used in makepkg
        var extensions = new[] { ".sig", ".sign", ".asc" };
        
        foreach (var ext in extensions)
        {
            if (signaturePath.EndsWith(ext))
            {
                var potentialPath = signaturePath.Substring(0, signaturePath.Length - ext.Length);
                if (File.Exists(potentialPath)) return potentialPath;
            }
        }
        return null;
    }
    
    /// <summary>
    /// Verifies the GPG signature of the HEAD commit in a git repository.
    /// </summary>
    public static bool VerifyGitCommit(string repoPath, string homeDir = null)
    {
        if (!Directory.Exists(repoPath)) return false;

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            // verify-commit HEAD: check signature of current commit
            // --raw: output in a machine-readable format similar to gpg's status-fd
            Arguments = "verify-commit --raw HEAD",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = repoPath
        };

        if (!string.IsNullOrEmpty(homeDir))
        {
            psi.EnvironmentVariables["GNUPGHOME"] = homeDir;
        }

        try
        {
            using var process = Process.Start(psi);
            if (process == null) return false;

            // git verify-commit outputs to stderr on success
            var output = process.StandardError.ReadToEnd();
            process.WaitForExit();

            // Success is indicated by the presence of "GOODSIG" in stderr
            bool isGood = output.Contains("gpg:                using RSA key") &&
                          output.Contains("gpg: Good signature from");

            if (!isGood)
            {
                AuLogger.Error($"GPG Verification Failed for git repo at {repoPath}");
                AuLogger.Debug($"Git Verify Output: {output}");
            }
            else
            {
                AuLogger.Debug($"Git commit signature valid for {repoPath}");
            }

            return isGood;
        }
        catch (Exception ex)
        {
            AuLogger.Error($"Failed to run git verify-commit: {ex.Message}");
            return false;
        }
    }
}