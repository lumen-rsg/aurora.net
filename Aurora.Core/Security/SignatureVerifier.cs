using Aurora.Core.Contract;
using Aurora.Core.Models;
using Spectre.Console;

namespace Aurora.Core.Security;

public class SignatureVerifier
{
    public void VerifySignatures(AuroraManifest manifest, string downloadDir, string startDir)
    {
        var sources = manifest.Build.Source;

        foreach (var sourceStr in sources)
        {
            var entry = new SourceEntry(sourceStr);
            string filePath = entry.Protocol == "local"
                ? Path.Combine(startDir, entry.FileName)
                : Path.Combine(downloadDir, entry.FileName);

            // Case 1: Standard detached signature file (.sig, .asc)
            if (entry.FileName.EndsWith(".sig") || entry.FileName.EndsWith(".asc"))
            {
                VerifyFileSignature(filePath);
            }
            
            // Case 2: VCS source marked as 'signed'
            else if (entry.IsSigned && entry.Protocol == "git")
            {
                VerifyGitSignature(filePath);
            }
        }
    }

    private void VerifyFileSignature(string sigPath)
    {
        var fileName = Path.GetFileName(sigPath);
        AnsiConsole.Markup($"  Verifying signature {fileName} ... ");

        var dataFile = GpgHelper.FindDataFileForSignature(sigPath);
        if (dataFile == null)
        {
            AnsiConsole.MarkupLine("[red]FAILED (Data file not found)[/]");
            throw new FileNotFoundException($"Could not find data file for signature {fileName}");
        }

        if (!GpgHelper.VerifySignature(dataFile, sigPath))
        {
            AnsiConsole.MarkupLine("[red]FAILED (Bad Signature)[/]");
            throw new Exception($"GPG Verification failed for {Path.GetFileName(dataFile)}");
        }

        AnsiConsole.MarkupLine("[green]Passed[/]");
    }

    private void VerifyGitSignature(string repoPath)
    {
        var repoName = Path.GetFileName(repoPath);
        AnsiConsole.Markup($"  Verifying git commit signature for {repoName} ... ");

        if (!GpgHelper.VerifyGitCommit(repoPath))
        {
            AnsiConsole.MarkupLine("[red]FAILED (Bad Commit Signature)[/]");
            throw new Exception($"GPG commit signature verification failed for repository {repoName}");
        }

        AnsiConsole.MarkupLine("[green]Passed[/]");
    }
}