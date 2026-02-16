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
        _sysRoot = Path.GetFullPath(sysRoot);
        LoadHooks();
    }

    private void LoadHooks()
    {
        var systemHooksDir = PathHelper.GetPath(_sysRoot, "usr/share/libalpm/hooks");
        var userHooksDir = PathHelper.GetPath(_sysRoot, "etc/pacman.d/hooks");
        var hookMap = new Dictionary<string, AlpmHook>();

        void ScanDir(string dir)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir, "*.hook"))
            {
                var h = HookParser.Parse(file);
                if (h != null) hookMap[h.Name] = h;
            }
        }

        ScanDir(systemHooksDir);
        ScanDir(userHooksDir);
        _allHooks.AddRange(hookMap.Values.OrderBy(h => h.Name));
    }

    public async Task RunHooksAsync(HookWhen when, List<Package> transactionPackages, TriggerOperation currentOp)
    {
        var applicableHooks = _allHooks.Where(h => h.When == when).ToList();
        if (applicableHooks.Count == 0) return;

        var changedFiles = new List<string>();
        foreach (var pkg in transactionPackages) changedFiles.AddRange(pkg.Files);

        foreach (var hook in applicableHooks)
        {
            var matchedTargets = new HashSet<string>();
            bool shouldRun = false;

            foreach (var trigger in hook.Triggers)
            {
                if (trigger.Operation != currentOp) continue;

                if (trigger.Type == TriggerType.Package)
                {
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
                    var regex = GlobToRegex(trigger.Target);
                    foreach (var file in changedFiles)
                    {
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
        AnsiConsole.MarkupLine($"[blue]:: Running hook:[/] {desc} ...");
        AuLogger.Info($"Executing hook: {hook.Name}");

        ProcessStartInfo psi;
        bool isChroot = _sysRoot != "/";

        if (isChroot)
        {
            // Bootstrap mode: Use host chroot
            psi = new ProcessStartInfo
            {
                FileName = "chroot",
                // We use /usr/bin/bash inside the chroot to execute the command string
                Arguments = $"\"{_sysRoot}\" /usr/bin/bash -c \"{hook.Exec}\"",
                RedirectStandardInput = hook.NeedsTargets,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }
        else
        {
            // Live mode
            psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{hook.Exec}\"",
                RedirectStandardInput = hook.NeedsTargets,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return;

            if (hook.NeedsTargets)
            {
                // Join with newlines for standard alpm behavior
                await proc.StandardInput.WriteAsync(string.Join("\n", targets));
                proc.StandardInput.Close();
            }

            proc.OutputDataReceived += (s, e) => { if (e.Data != null && !string.IsNullOrWhiteSpace(e.Data)) AnsiConsole.MarkupLine($"    [grey]{Markup.Escape(e.Data)}[/]"); };
            proc.ErrorDataReceived += (s, e) => { if (e.Data != null && !string.IsNullOrWhiteSpace(e.Data)) AnsiConsole.MarkupLine($"    [red]{Markup.Escape(e.Data)}[/]"); };

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning:[/] Hook {hook.Name} failed (Exit Code {proc.ExitCode})");
                if (hook.AbortOnFail) throw new Exception($"Critical hook {hook.Name} failed.");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Hook Error:[/] {ex.Message}");
            if (hook.AbortOnFail) throw;
        }
    }

    private Regex GlobToRegex(string glob)
    {
        var pattern = "^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}