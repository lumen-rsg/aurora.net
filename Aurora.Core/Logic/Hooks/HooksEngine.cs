using System.Diagnostics;
using System.Text.RegularExpressions;
using Aurora.Core.IO;
using Aurora.Core.Models;
using Aurora.Core.Logging;
using Spectre.Console;

namespace Aurora.Core.Logic.Hooks;

public class HookEngine
{
    private readonly string _sysRoot;
    private readonly List<AlpmHook> _allHooks = new();

    public HookEngine(string sysRoot)
    {
        _sysRoot = sysRoot;
        LoadHooks();
    }

    private void LoadHooks()
    {
        // 1. Define Paths (Standard Arch paths)
        var systemHooksDir = PathHelper.GetPath(_sysRoot, "usr/share/libalpm/hooks");
        var userHooksDir = PathHelper.GetPath(_sysRoot, "etc/pacman.d/hooks");

        var hookMap = new Dictionary<string, AlpmHook>();

        // 2. Load System Hooks
        if (Directory.Exists(systemHooksDir))
        {
            foreach (var file in Directory.GetFiles(systemHooksDir, "*.hook"))
            {
                var h = HookParser.Parse(file);
                if (h != null) hookMap[h.Name] = h;
            }
        }

        // 3. Load User Hooks (Override system hooks with same name)
        if (Directory.Exists(userHooksDir))
        {
            foreach (var file in Directory.GetFiles(userHooksDir, "*.hook"))
            {
                var h = HookParser.Parse(file);
                if (h != null) hookMap[h.Name] = h;
            }
        }

        // 4. Sort lexically (standard alpm behavior)
        _allHooks.AddRange(hookMap.Values.OrderBy(h => h.Name));
    }

    public async Task RunHooksAsync(HookWhen when, List<Package> transactionPackages, TriggerOperation currentOp)
    {
        var applicableHooks = _allHooks.Where(h => h.When == when).ToList();
        if (applicableHooks.Count == 0) return;

        // Pre-calculate file lists for "File" triggers
        var changedFiles = new List<string>();
        foreach (var pkg in transactionPackages)
        {
            // Note: In a real update, we should distinguish files being removed vs installed.
            // For now, we aggregate all files in the transaction scope.
            changedFiles.AddRange(pkg.Files);
        }

        AnsiConsole.MarkupLine($"[grey]Checking {applicableHooks.Count} hooks ({when})...[/]");

        foreach (var hook in applicableHooks)
        {
            var matchedTargets = new HashSet<string>();
            bool shouldRun = false;

            foreach (var trigger in hook.Triggers)
            {
                // 1. Filter by Operation (Install/Upgrade/Remove)
                // Note: In a real mixed transaction, we'd check per package. 
                // Currently simplifying assuming uniform transaction op.
                if (trigger.Operation != currentOp) continue;

                if (trigger.Type == TriggerType.Package)
                {
                    // Check if target package is in transaction
                    foreach (var pkg in transactionPackages)
                    {
                        if (pkg.Name == trigger.Target)
                        {
                            shouldRun = true;
                            if (hook.NeedsTargets) matchedTargets.Add(pkg.Name);
                        }
                    }
                }
                else if (trigger.Type == TriggerType.File)
                {
                    // Convert Glob to Regex
                    var regex = GlobToRegex(trigger.Target);
                    
                    foreach (var file in changedFiles)
                    {
                        // Strip leading slash for matching logic if needed, 
                        // but usually alpm paths are relative or absolute? 
                        // Arch hooks usually look like "usr/lib/..." (no leading slash)
                        var cleanFile = file.TrimStart('/');
                        
                        if (regex.IsMatch(cleanFile))
                        {
                            shouldRun = true;
                            if (hook.NeedsTargets) matchedTargets.Add(file);
                        }
                    }
                }
            }

            if (shouldRun)
            {
                await ExecuteHookAsync(hook, matchedTargets);
            }
        }
    }

    private async Task ExecuteHookAsync(AlpmHook hook, HashSet<string> targets)
    {
        var desc = string.IsNullOrEmpty(hook.Description) ? hook.Name : hook.Description;
        AnsiConsole.MarkupLine($"[blue] -> Running hook:[/] {desc} ...");
        AuLogger.Info($"Executing hook: {hook.Name}");

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"-c \"{hook.Exec}\"",
            RedirectStandardInput = hook.NeedsTargets,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Important: Hooks run inside the SysRoot context? 
        // Arch hooks assume they run on the live system.
        // If we are bootstrapping to a directory (--bootstrap), we might need `chroot`.
        // For now, we assume standard execution, but we might prepend the sysroot to paths logic later.
        
        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return;

            // Handle NeedsTargets (Pipe file list to stdin)
            if (hook.NeedsTargets)
            {
                await proc.StandardInput.WriteAsync(string.Join("\n", targets));
                proc.StandardInput.Close();
            }

            // Capture output to log, maybe show spinner
            var output = await proc.StandardOutput.ReadToEndAsync();
            var error = await proc.StandardError.ReadToEndAsync();
            
            await proc.WaitForExitAsync();

            if (!string.IsNullOrWhiteSpace(output)) AuLogger.Debug($"[Hook Output] {output}");

            if (proc.ExitCode != 0)
            {
                AnsiConsole.MarkupLine($"[red]Hook failed:[/] {error}");
                AuLogger.Error($"Hook {hook.Name} failed: {error}");
                
                if (hook.AbortOnFail)
                {
                    throw new Exception($"Critical hook {hook.Name} failed.");
                }
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error executing hook:[/] {ex.Message}");
            if (hook.AbortOnFail) throw;
        }
    }

    private Regex GlobToRegex(string glob)
    {
        // Simple Glob to Regex converter for path matching
        // Escapes dots, converts * to .*, etc.
        var pattern = "^" + Regex.Escape(glob)
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".") + "$";
        
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}