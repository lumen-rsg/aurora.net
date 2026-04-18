using System.Diagnostics;
using System.Text;
using Aurora.Core.IO;
using Aurora.Core.Logic;
using Aurora.Core.Logging;
using Aurora.Core.Models;
using Aurora.Core.Net;
using Aurora.Core.State;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class InstallSystemCommand : ICommand
{
    public string Name => "system-install";
    public string Description => "Install Lumina Linux to a target block device";

    private const string MountPoint = "/mnt";

    // Base system packages
    private static readonly string[] BasePackages =
    [
        "filesystem", "setup", "basesystem", "coreutils",
        "bash", "systemd", "dbus-broker", "iproute",
        "passwd", "shadow-utils", "util-linux",
        "grub2", "grub2-efi-x64", "grub2-efi-x64-modules", "grub2-tools", "shim-x64", "efibootmgr",
        "linux-firmware", "kernel-core", "kernel-modules",
        "vim-minimal", "nano", "less", "which", "findutils",
        "procps-ng", "psmisc", "hostname", "iputils", "curl",
        "NetworkManager", "wpa_supplicant", "NetworkManager-wifi",
        "sudo"
    ];

    public async Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        // ─── Step 0: Root Check & Welcome ──────────────────────────────
        if (!OperatingSystem.IsLinux())
        {
            AnsiConsole.MarkupLine("[red]This command can only run on Linux.[/]");
            return;
        }

        if (Syscall.geteuid() != 0)
        {
            AnsiConsole.MarkupLine("[red bold]Error:[/] This command must be run as root.");
            return;
        }

        AnsiConsole.Write(new FigletText("Aurora Installer").Color(Color.Cyan1));
        AnsiConsole.MarkupLine("[grey]Lumina Linux System Installer[/]");
        AnsiConsole.WriteLine();

        // ─── Step 1: Partition Mode Selection ──────────────────────────
        var mode = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Select partitioning mode:[/]")
                .AddChoices("Automatic", "Manual"));

        string baseDevice;
        string efiPart, bootPart, rootPart;

        if (mode == "Automatic")
        {
            var result = AutomaticPartitioning();
            if (result == null) return;
            (baseDevice, efiPart, bootPart, rootPart) = result.Value;
        }
        else
        {
            var result = ManualPartitioning();
            if (result == null) return;
            (baseDevice, efiPart, bootPart, rootPart) = result.Value;
        }

        // ─── Step 2: Mount Partitions ──────────────────────────────────
        AnsiConsole.MarkupLine($"\n[bold blue]Step 2:[/] Mounting partitions...");
        try
        {
            RunCommand("mount", $"\"{rootPart}\" \"{MountPoint}\"", "Failed to mount root partition");
            RunCommand("mkdir", "-p /mnt/boot", "Failed to create /mnt/boot");
            RunCommand("mount", $"\"{bootPart}\" \"{MountPoint}/boot\"", "Failed to mount boot partition");
            RunCommand("mkdir", "-p /mnt/boot/efi", "Failed to create /mnt/boot/efi");
            RunCommand("mount", $"\"{efiPart}\" \"{MountPoint}/boot/efi\"", "Failed to mount EFI partition");
            AnsiConsole.MarkupLine("[green bold]✔ Partitions mounted successfully.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Mount failed: {Markup.Escape(ex.Message)}[/]");
            return;
        }

        // ─── Step 3: Initialize Root ───────────────────────────────────
        AnsiConsole.MarkupLine($"\n[bold blue]Step 3:[/] Initializing root environment...");
        var targetConfig = new CliConfiguration(MountPoint, force: true, assumeYes: true,
            skipGpg: true, noRecommends: false, skipDownload: false);

        var initCmd = new InitCommand();
        await initCmd.ExecuteAsync(targetConfig, Array.Empty<string>());

        // ─── Step 4: Create Repo File ──────────────────────────────────
        AnsiConsole.MarkupLine($"\n[bold blue]Step 4:[/] Creating repository configuration...");
        var repoFilePath = Path.Combine(MountPoint, "etc/yum.repos.d/lumina.repo");
        await File.WriteAllTextAsync(repoFilePath, GetLuminaRepoStub());
        AnsiConsole.MarkupLine("[green bold]✔ Repository file created.[/]");

        // ─── Step 5: Sync Repositories ─────────────────────────────────
        AnsiConsole.MarkupLine($"\n[bold blue]Step 5:[/] Synchronizing repositories...");
        var syncCmd = new SyncCommand();
        await syncCmd.ExecuteAsync(targetConfig, Array.Empty<string>());

        // ─── Bind-mount kernel virtual filesystems ────────────────────
        // Required for RPM post-install scripts (kernel initramfs, grub2-probe,
        // udev hwdb, systemd catalog) and grub2-install inside chroot.
        AnsiConsole.MarkupLine($"\n[bold blue]Mounting virtual filesystems...[/]");
        try
        {
            RunCommand("mkdir", $"-p {MountPoint}/dev {MountPoint}/proc {MountPoint}/sys", "Failed to create virtual fs directories");
            RunCommand("mount", $"--bind /dev {MountPoint}/dev", "Failed to bind-mount /dev");
            RunCommand("mount", $"--bind /proc {MountPoint}/proc", "Failed to bind-mount /proc");
            RunCommand("mount", $"--bind /sys {MountPoint}/sys", "Failed to bind-mount /sys");
            AnsiConsole.MarkupLine("[green bold]✔ Virtual filesystems mounted.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Virtual filesystem mount failed: {Markup.Escape(ex.Message)}[/]");
            return;
        }

        // ─── Step 6: Install Base System ───────────────────────────────
        AnsiConsole.MarkupLine($"\n[bold blue]Step 6:[/] Installing base system...");
        await InstallBaseSystem(targetConfig);

        // ─── Step 7: Additional Package Selection ──────────────────────
        AnsiConsole.MarkupLine($"\n[bold blue]Step 7:[/] Additional packages...");
        await SelectAndInstallGroups(targetConfig);

        // ─── Rebuild linker cache ───────────────────────────────────────
        AnsiConsole.MarkupLine($"\n[bold blue]Rebuilding linker cache...[/]");
        try
        {
            RunCommand("ldconfig", $"-r \"{MountPoint}\"", "Failed to rebuild linker cache");
            AnsiConsole.MarkupLine("[green bold]✔ Linker cache rebuilt.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning: ldconfig failed: {Markup.Escape(ex.Message)}[/]");
        }

        // ─── Step 8: Install GRUB2 ─────────────────────────────────────
        AnsiConsole.MarkupLine($"\n[bold blue]Step 8:[/] Installing bootloader...");
        InstallGrub(baseDevice);

        // ─── Cleanup: Unmount virtual filesystems ──────────────────────
        // No longer needed after GRUB install; package scriptlets are done.
        AnsiConsole.MarkupLine($"\n[bold blue]Cleaning up virtual filesystems...[/]");
        try
        {
            RunCommand("umount", $"{MountPoint}/sys", "Failed to unmount /mnt/sys");
            RunCommand("umount", $"{MountPoint}/proc", "Failed to unmount /mnt/proc");
            RunCommand("umount", $"{MountPoint}/dev", "Failed to unmount /mnt/dev");
            AnsiConsole.MarkupLine("[green bold]✔ Virtual filesystems unmounted.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning: Virtual filesystem cleanup failed: {Markup.Escape(ex.Message)}[/]");
        }

        // ─── Step 9: User & Password Setup ─────────────────────────────
        AnsiConsole.MarkupLine($"\n[bold blue]Step 9:[/] User setup...");
        SetupUser();

        // ─── Step 10: fstab & Finalization ─────────────────────────────
        AnsiConsole.MarkupLine($"\n[bold blue]Step 10:[/] Finalizing...");
        await GenerateFstab(efiPart, bootPart, rootPart);

        // Copy DNS configuration from host
        try
        {
            var hostResolv = "/etc/resolv.conf";
            var targetResolv = Path.Combine(MountPoint, "etc/resolv.conf");
            if (File.Exists(hostResolv))
            {
                File.Copy(hostResolv, targetResolv, true);
                AnsiConsole.MarkupLine("[green bold]✔ DNS configuration copied.[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning: Failed to copy resolv.conf: {Markup.Escape(ex.Message)}[/]");
        }

        // Configure sudoers for wheel group
        try
        {
            var sudoersDir = Path.Combine(MountPoint, "etc/sudoers.d");
            Directory.CreateDirectory(sudoersDir);
            var wheelSudoers = Path.Combine(sudoersDir, "wheel");
            await File.WriteAllTextAsync(wheelSudoers, "%wheel ALL=(ALL) ALL\n");
            RunCommand("chmod", $"440 \"{wheelSudoers}\"", "Failed to set sudoers permissions");

            // Ensure /etc/sudoers includes sudoers.d
            var sudoersPath = Path.Combine(MountPoint, "etc/sudoers");
            if (File.Exists(sudoersPath))
            {
                var sudoersContent = await File.ReadAllTextAsync(sudoersPath);
                if (!sudoersContent.Contains("@includedir /etc/sudoers.d") &&
                    !sudoersContent.Contains("#includedir /etc/sudoers.d"))
                {
                    await File.AppendAllTextAsync(sudoersPath, "\n@includedir /etc/sudoers.d\n");
                }
            }

            AnsiConsole.MarkupLine("[green bold]✔ Sudoers configured for wheel group.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning: Failed to configure sudoers: {Markup.Escape(ex.Message)}[/]");
        }

        // Done!
        AnsiConsole.Write(new Rule("[green bold]Installation Complete[/]").RuleStyle("green"));
        AnsiConsole.MarkupLine("[green bold]✔ Lumina Linux has been installed successfully![/]");
        AnsiConsole.MarkupLine("[grey]You may now reboot, or unmount /mnt manually.[/]");
        AnsiConsole.MarkupLine("[grey]To reboot: shutdown -r now[/]");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  STEP 1A: Automatic Partitioning
    // ═══════════════════════════════════════════════════════════════════

    private (string baseDev, string efi, string boot, string root)? AutomaticPartitioning()
    {
        AnsiConsole.MarkupLine("\n[bold blue]Step 1:[/] Automatic partitioning");

        var baseDevice = AnsiConsole.Ask<string>(
            "[bold]Enter target block device[/] (e.g. [grey]/dev/sda[/] or [grey]/dev/nvme0n1[/]):");

        baseDevice = baseDevice.Trim();

        // Validate device exists
        if (!File.Exists(baseDevice))
        {
            AnsiConsole.MarkupLine($"[red]Device {Markup.Escape(baseDevice)} does not exist.[/]");
            return null;
        }

        // Compute partition names
        var (efiPart, bootPart, rootPart) = GetPartitionNames(baseDevice);

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Table().Border(TableBorder.Rounded)
            .AddColumn("Partition")
            .AddColumn("Device")
            .AddColumn("Size")
            .AddColumn("Type")
            .AddRow("/boot/efi", $"[cyan]{efiPart}[/]", "600M", "FAT32")
            .AddRow("/boot", $"[cyan]{bootPart}[/]", "1G", "ext4")
            .AddRow("/", $"[cyan]{rootPart}[/]", "Remaining", "ext4"));

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold red on yellow]⚠ WARNING: This will DESTROY ALL DATA on the target device! ⚠[/]");
        AnsiConsole.MarkupLine($"[red]All data on [bold]{Markup.Escape(baseDevice)}[/] will be permanently lost.[/]");

        var confirm = AnsiConsole.Ask<string>("Type [bold]Y[/] to continue:");
        if (!confirm.Equals("Y", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("[yellow]Aborted.[/]");
            return null;
        }

        // Execute partitioning
        AnsiConsole.Status().Start("[cyan]Partitioning device...[/]", ctx =>
        {
            ctx.Status("[cyan]Wiping existing partition table...[/]");
            RunCommand("sgdisk", $"--zap-all \"{baseDevice}\"", "Failed to wipe partition table");

            ctx.Status("[cyan]Creating EFI partition (600M)...[/]");
            RunCommand("sgdisk", $"-n 1::+600M -t 1:EF00 \"{baseDevice}\"", "Failed to create EFI partition");

            ctx.Status("[cyan]Creating boot partition (1G)...[/]");
            RunCommand("sgdisk", $"-n 2::+1G -t 2:8300 \"{baseDevice}\"", "Failed to create boot partition");

            ctx.Status("[cyan]Creating root partition (remaining space)...[/]");
            RunCommand("sgdisk", $"-n 3:: -t 3:8300 \"{baseDevice}\"", "Failed to create root partition");

            ctx.Status("[cyan]Formatting EFI partition (FAT32)...[/]");
            RunCommand("mkfs.vfat", $"-F32 \"{efiPart}\"", "Failed to format EFI partition");

            ctx.Status("[cyan]Formatting boot partition (ext4)...[/]");
            RunCommand("mkfs.ext4", $"-F \"{bootPart}\"", "Failed to format boot partition");

            ctx.Status("[cyan]Formatting root partition (ext4)...[/]");
            RunCommand("mkfs.ext4", $"-F \"{rootPart}\"", "Failed to format root partition");
        });

        AnsiConsole.MarkupLine("[green bold]✔ Partitions created and formatted.[/]");
        return (baseDevice, efiPart, bootPart, rootPart);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  STEP 1B: Manual Partitioning
    // ═══════════════════════════════════════════════════════════════════

    private (string baseDev, string efi, string boot, string root)? ManualPartitioning()
    {
        AnsiConsole.MarkupLine("\n[bold blue]Step 1:[/] Manual partitioning");
        AnsiConsole.MarkupLine("[grey]Provide the block devices for each mount point.[/]");

        var efiPart = AnsiConsole.Ask<string>("[bold]/boot/efi[/] partition (e.g. [grey]/dev/sda1[/]):");
        var bootPart = AnsiConsole.Ask<string>("[bold]/boot[/] partition (e.g. [grey]/dev/sda2[/]):");
        var rootPart = AnsiConsole.Ask<string>("[bold]/[/] (root) partition (e.g. [grey]/dev/sda3[/]):");

        efiPart = efiPart.Trim();
        bootPart = bootPart.Trim();
        rootPart = rootPart.Trim();

        // Validate all exist
        foreach (var dev in new[] { efiPart, bootPart, rootPart })
        {
            if (!File.Exists(dev))
            {
                AnsiConsole.MarkupLine($"[red]Device {Markup.Escape(dev)} does not exist.[/]");
                return null;
            }
        }

        // Try to infer base device from root partition (strip trailing partition suffix)
        string baseDevice = InferBaseDevice(rootPart);

        AnsiConsole.MarkupLine("[green bold]✔ Manual partition configuration accepted.[/]");
        return (baseDevice, efiPart, bootPart, rootPart);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  STEP 6: Install Base System
    // ═══════════════════════════════════════════════════════════════════

    private async Task InstallBaseSystem(CliConfiguration config)
    {
        var repoFiles = RepoLoader.DiscoverRepoDatabases(config.RepoDir);
        if (repoFiles.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No repository databases found. Sync may have failed.");
            return;
        }

        var availablePackages = await AnsiConsole.Status().StartAsync("Reading repositories...", _ =>
            Task.FromResult(RepoLoader.LoadAllPackages(config.RepoDir)));

        AnsiConsole.MarkupLine($"[grey]Loaded {availablePackages.Count} packages from {repoFiles.Length} repositories.[/]");

        // Resolve dependencies for base packages
        List<Package> plan;
        var installedPkgs = RpmLocalDb.GetInstalledPackages(config.SysRoot);
        var sw = Stopwatch.StartNew();

        try
        {
            var solver = new DependencySolver(availablePackages, installedPkgs);
            int resolvedCount = 0;
            plan = AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots2)
                .Start("[cyan]Resolving base system dependencies...[/]", ctx =>
                {
                    var result = solver.Resolve(BasePackages.ToList(), (count, name) =>
                    {
                        resolvedCount = count;
                        ctx.Status($"[cyan]Resolving base system dependencies...[/] [grey]({count} resolved)[/]");
                    }, resolveRecommends: !config.NoRecommends);
                    return result;
                });

            sw.Stop();
            AnsiConsole.MarkupLine(
                $"[green bold]✔[/] Resolved [bold]{plan.Count}[/] packages in [grey]{sw.Elapsed.TotalSeconds:F1}s[/]");
        }
        catch (Exception ex)
        {
            sw.Stop();
            AnsiConsole.MarkupLine($"[red bold]Dependency Error:[/] {Markup.Escape(ex.Message)}");
            return;
        }

        // Download packages
        var repoMgr = new RepoManager(config.SysRoot) { SkipSignatureCheck = true };
        var packagePaths = new string[plan.Count];
        var semaphore = new SemaphoreSlim(5);

        if (!Directory.Exists(config.CacheDir)) Directory.CreateDirectory(config.CacheDir);

        await AnsiConsole.Progress()
            .Columns(new ProgressColumn[]
            {
                new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(),
                new DownloadedColumn(), new SpinnerColumn()
            })
            .StartAsync(async ctx =>
            {
                var tasks = plan.Select(async (pkg, index) =>
                {
                    await semaphore.WaitAsync();
                    var task = ctx.AddTask($"[grey]{pkg.Name}[/]");
                    try
                    {
                        var path = await repoMgr.DownloadPackageAsync(pkg, config.CacheDir, (total, current) =>
                        {
                            if (total.HasValue)
                            {
                                task.MaxValue = total.Value;
                                task.Value = current;
                            }
                            else task.IsIndeterminate = true;
                        });

                        if (path == null) throw new FileNotFoundException($"Package {pkg.Name} not found.");
                        packagePaths[index] = path;
                    }
                    finally
                    {
                        task.StopTask();
                        semaphore.Release();
                    }
                });
                await Task.WhenAll(tasks);
            });

        // Install via RPM
        AnsiConsole.MarkupLine("[cyan]Installing base system packages...[/]");
        var rpmLogs = new List<string>();
        try
        {
            AnsiConsole.Status().Start("[cyan]Installing packages...[/]", ctx =>
            {
                SystemUpdater.ApplyUpdates(packagePaths, config.SysRoot, force: false, skipGpg: true,
                    msg => rpmLogs.Add(msg));
            });
            AnsiConsole.MarkupLine("[green bold]✔ Base system installed successfully.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red bold]Base system installation failed:[/] {Markup.Escape(ex.Message)}");
            if (rpmLogs.Count > 0)
            {
                AnsiConsole.Write(new Rule("[yellow]RPM Output[/]").RuleStyle("yellow"));
                foreach (var log in rpmLogs)
                    AnsiConsole.MarkupLine($"[grey]{Markup.Escape(log)}[/]");
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  STEP 7: Additional Package Group Selection
    // ═══════════════════════════════════════════════════════════════════

    private async Task SelectAndInstallGroups(CliConfiguration config)
    {
        var groups = RepoManager.LoadAllGroups(config.SysRoot);

        if (groups.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No package groups available. Skipping additional package selection.[/]");
            return;
        }

        // Build display list
        var groupChoices = groups
            .Where(g => g.Uservisible && g.DefaultPackages.Any())
            .Select(g => $"{g.Name} ({g.Id})")
            .ToList();

        if (groupChoices.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No installable groups found.[/]");
            return;
        }

        var selected = AnsiConsole.Prompt(
            new MultiSelectionPrompt<string>()
                .Title("[bold]Select additional package groups to install:[/]")
                .PageSize(15)
                .AddChoices(groupChoices));

        if (selected.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No additional groups selected.[/]");
            return;
        }

        // Extract group IDs from selections
        var groupIds = selected
            .Select(s => s[(s.LastIndexOf('(') + 1)..s.LastIndexOf(')')])
            .ToList();

        AnsiConsole.MarkupLine($"[blue]Installing {groupIds.Count} additional group(s)...[/]");

        // Load packages and resolve each group
        var availablePackages = RepoLoader.LoadAllPackages(config.RepoDir);
        var installedPkgs = RpmLocalDb.GetInstalledPackages(config.SysRoot);
        var installedNames = installedPkgs.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var allTargets = new List<string>();

        foreach (var groupId in groupIds)
        {
            var group = groups.FirstOrDefault(g =>
                g.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));

            if (group == null) continue;

            var groupPkgNames = group.DefaultPackages
                .Select(p => p.Name)
                .Where(n => !installedNames.Contains(n))
                .Distinct();

            allTargets.AddRange(groupPkgNames);
        }

        allTargets = allTargets.Distinct().ToList();

        if (allTargets.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]All group packages are already installed.[/]");
            return;
        }

        // Resolve deps
        List<Package> plan;
        try
        {
            var solver = new DependencySolver(availablePackages, installedPkgs);
            plan = AnsiConsole.Status()
                .Start("[cyan]Resolving group dependencies...[/]", ctx => solver.Resolve(allTargets, (_, _) => { }));
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red bold]Dependency Error:[/] {Markup.Escape(ex.Message)}");
            return;
        }

        // Download
        var repoMgr = new RepoManager(config.SysRoot) { SkipSignatureCheck = true };
        var packagePaths = new string[plan.Count];
        var semaphore = new SemaphoreSlim(5);

        if (!Directory.Exists(config.CacheDir)) Directory.CreateDirectory(config.CacheDir);

        await AnsiConsole.Progress()
            .Columns(new ProgressColumn[]
            {
                new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(),
                new DownloadedColumn(), new SpinnerColumn()
            })
            .StartAsync(async ctx =>
            {
                var tasks = plan.Select(async (pkg, index) =>
                {
                    await semaphore.WaitAsync();
                    var task = ctx.AddTask($"[grey]{pkg.Name}[/]");
                    try
                    {
                        var path = await repoMgr.DownloadPackageAsync(pkg, config.CacheDir, (total, current) =>
                        {
                            if (total.HasValue)
                            {
                                task.MaxValue = total.Value;
                                task.Value = current;
                            }
                            else task.IsIndeterminate = true;
                        });

                        if (path == null) throw new FileNotFoundException($"Package {pkg.Name} not found.");
                        packagePaths[index] = path;
                    }
                    finally
                    {
                        task.StopTask();
                        semaphore.Release();
                    }
                });
                await Task.WhenAll(tasks);
            });

        // Install
        var rpmLogs = new List<string>();
        try
        {
            AnsiConsole.Status().Start("[cyan]Installing group packages...[/]", ctx =>
            {
                SystemUpdater.ApplyUpdates(packagePaths, config.SysRoot, force: false, skipGpg: true,
                    msg => rpmLogs.Add(msg));
            });
            AnsiConsole.MarkupLine("[green bold]✔ Additional packages installed.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red bold]Group installation failed:[/] {Markup.Escape(ex.Message)}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  STEP 8: Install GRUB2
    // ═══════════════════════════════════════════════════════════════════

    private void InstallGrub(string baseDevice)
    {
        if (string.IsNullOrEmpty(baseDevice))
        {
            AnsiConsole.MarkupLine("[yellow]Warning: Cannot determine base device for GRUB. Skipping bootloader install.[/]");
            AnsiConsole.MarkupLine("[yellow]You will need to install GRUB manually.[/]");
            return;
        }

        try
        {
            AnsiConsole.Status().Start("[cyan]Installing GRUB2 bootloader...[/]", ctx =>
            {
                RunCommand("chroot",
                    $"{MountPoint} grub2-install --target=x86_64-efi " +
                    "--efi-directory=/boot/efi --boot-directory=/boot/grub2 " +
                    $"--no-nvram --removable --force \"{baseDevice}\"",
                    "Failed to install GRUB2");

                ctx.Status("[cyan]Generating GRUB configuration...[/]");
                RunCommand("chroot",
                    $"{MountPoint} grub2-mkconfig -o /boot/grub2/grub.cfg",
                    "Failed to generate GRUB configuration");
            });

            AnsiConsole.MarkupLine("[green bold]✔ GRUB2 bootloader installed.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]GRUB installation failed: {Markup.Escape(ex.Message)}[/]");
            AnsiConsole.MarkupLine("[yellow]You may need to install the bootloader manually.[/]");
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  STEP 9: User & Password Setup
    // ═══════════════════════════════════════════════════════════════════

    private void SetupUser()
    {
        // Ask for admin username
        var username = AnsiConsole.Ask<string>("[bold]Enter admin username:[/]");
        username = username.Trim();

        if (string.IsNullOrWhiteSpace(username))
        {
            AnsiConsole.MarkupLine("[red]Invalid username. Skipping user creation.[/]");
            return;
        }

        // Get the next available UID (starting from 1000)
        int uid = GetNextUid();

        // Hash passwords using openssl
        var userPassword = AnsiConsole.Prompt(
            new TextPrompt<string>($"[bold]Enter password for {Markup.Escape(username)}:[/]")
                .Secret());

        var rootPassword = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]Enter root password:[/]")
                .Secret());

        // Hash passwords
        string userHash = HashPassword(userPassword);
        string rootHash = HashPassword(rootPassword);

        if (userHash == "!" || rootHash == "!")
        {
            AnsiConsole.MarkupLine("[red]Failed to hash passwords. User setup incomplete.[/]");
            return;
        }

        // Write to /etc/group
        var groupPath = Path.Combine(MountPoint, "etc/group");
        var groupLines = File.Exists(groupPath) ? File.ReadAllLines(groupPath).ToList() : new List<string>();

        // Ensure wheel group exists
        if (!groupLines.Any(l => l.StartsWith("wheel:")))
            groupLines.Add("wheel:x:10:");

        // Ensure users group exists (GID 100 - standard users group)
        if (!groupLines.Any(l => l.StartsWith("users:")))
            groupLines.Add("users:x:100:");

        // Add user private group
        groupLines.Add($"{username}:x:{uid}:");
        // Add user to wheel group
        for (int i = 0; i < groupLines.Count; i++)
        {
            if (groupLines[i].StartsWith("wheel:"))
            {
                var parts = groupLines[i].Split(':');
                if (parts.Length >= 4)
                    parts[3] = string.IsNullOrEmpty(parts[3]) ? username : $"{parts[3]},{username}";
                else
                    parts = [.. parts, username];
                groupLines[i] = string.Join(":", parts);
                break;
            }
        }

        File.WriteAllLines(groupPath, groupLines);

        // Write to /etc/passwd
        var passwdPath = Path.Combine(MountPoint, "etc/passwd");
        var passwdLines = File.Exists(passwdPath) ? File.ReadAllLines(passwdPath).ToList() : new List<string>();
        passwdLines.Add($"{username}:x:{uid}:100:{username}:/home/{username}:/bin/bash");
        File.WriteAllLines(passwdPath, passwdLines);

        // Write to /etc/shadow
        var shadowPath = Path.Combine(MountPoint, "etc/shadow");
        var shadowLines = File.Exists(shadowPath) ? File.ReadAllLines(shadowPath).ToList() : new List<string>();

        // Update root password
        for (int i = 0; i < shadowLines.Count; i++)
        {
            if (shadowLines[i].StartsWith("root:"))
            {
                var parts = shadowLines[i].Split(':');
                parts[1] = rootHash;
                shadowLines[i] = string.Join(":", parts);
                break;
            }
        }

        // Add user shadow entry
        shadowLines.Add($"{username}:{userHash}:{(int)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalDays}::99999:7:::");
        File.WriteAllLines(shadowPath, shadowLines);

        // Create home directory
        var homeDir = Path.Combine(MountPoint, "home", username);
        Directory.CreateDirectory(homeDir);

        // Copy skeleton files if available
        var skelDir = Path.Combine(MountPoint, "etc/skel");
        if (Directory.Exists(skelDir))
        {
            foreach (var file in Directory.GetFiles(skelDir, "*", SearchOption.AllDirectories))
            {
                var relative = file.Substring(skelDir.Length).TrimStart('/');
                var target = Path.Combine(homeDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
            }
        }

        // Fix ownership of home directory (GID 100 = users group)
        try
        {
            RunCommand("chown", $"-R {uid}:100 \"{homeDir}\"", "Failed to set home directory ownership");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning: Failed to set home directory ownership: {Markup.Escape(ex.Message)}[/]");
        }

        AnsiConsole.MarkupLine($"[green bold]✔ User '{Markup.Escape(username)}' created with wheel group access.[/]");
        AnsiConsole.MarkupLine("[green bold]✔ Root password set.[/]");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  STEP 10: fstab Generation
    // ═══════════════════════════════════════════════════════════════════

    private async Task GenerateFstab(string efiPart, string bootPart, string rootPart)
    {
        var fstabPath = Path.Combine(MountPoint, "etc/fstab");
        var sb = new StringBuilder();
        sb.AppendLine("# /etc/fstab - Generated by Aurora Installer");
        sb.AppendLine("#");
        sb.AppendLine("# <file system>  <mount point>  <type>  <options>  <dump>  <pass>");
        sb.AppendLine();

        // Get UUIDs for each partition
        string rootUuid = GetBlkid(rootPart);
        string bootUuid = GetBlkid(bootPart);
        string efiUuid = GetBlkid(efiPart);

        sb.AppendLine($"UUID={rootUuid}  /              ext4    defaults        0  1");
        sb.AppendLine($"UUID={bootUuid}  /boot          ext4    defaults        0  2");
        sb.AppendLine($"UUID={efiUuid}   /boot/efi      vfat    umask=0077      0  2");

        await File.WriteAllTextAsync(fstabPath, sb.ToString());
        AnsiConsole.MarkupLine("[green bold]✔ /etc/fstab generated.[/]");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  UTILITY HELPERS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    ///     Returns partition device names, handling NVMe/MMC vs SATA naming.
    ///     NVMe:  /dev/nvme0n1 -> /dev/nvme0n1p1, /dev/nvme0n1p2, /dev/nvme0n1p3
    ///     SATA:  /dev/sda     -> /dev/sda1, /dev/sda2, /dev/sda3
    /// </summary>
    private static (string efi, string boot, string root) GetPartitionNames(string baseDevice)
    {
        string suffix = char.IsDigit(baseDevice[^1]) ? "p" : "";
        return (
            $"{baseDevice}{suffix}1",
            $"{baseDevice}{suffix}2",
            $"{baseDevice}{suffix}3"
        );
    }

    /// <summary>
    ///     Infers the base block device from a partition device name.
    ///     /dev/nvme0n1p3 -> /dev/nvme0n1
    ///     /dev/sda3      -> /dev/sda
    ///     /dev/mmcblk0p3 -> /dev/mmcblk0
    /// </summary>
    private static string InferBaseDevice(string partitionDev)
    {
        // Handle NVMe and MMC: strip "pN" suffix
        if (partitionDev.Contains("nvme") || partitionDev.Contains("mmcblk"))
        {
            var lastP = partitionDev.LastIndexOf('p');
            if (lastP > 0 && lastP > partitionDev.LastIndexOf('/'))
                return partitionDev[..lastP];
        }

        // Handle SATA/SCSI: strip trailing digit(s)
        int i = partitionDev.Length - 1;
        while (i >= 0 && char.IsDigit(partitionDev[i])) i--;
        if (i < partitionDev.Length - 1)
            return partitionDev[..(i + 1)];

        return partitionDev;
    }

    /// <summary>
    ///     Runs an external command and throws on non-zero exit.
    /// </summary>
    private static void RunCommand(string binary, string arguments, string errorMessage)
    {
        var psi = new ProcessStartInfo
        {
            FileName = binary,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null) throw new Exception($"Failed to start {binary}");

        var err = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            AuLogger.Error($"{binary} {arguments}: exit {proc.ExitCode} - {err}");
            throw new Exception($"{errorMessage} (exit {proc.ExitCode}): {err}");
        }
    }

    /// <summary>
    ///     Hashes a password using SHA-512 via openssl.
    /// </summary>
    private static string HashPassword(string password)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "openssl",
                Arguments = "passwd -6 -stdin",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return "!";

            proc.StandardInput.WriteLine(password);
            proc.StandardInput.Close();
            var hash = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();

            return proc.ExitCode == 0 ? hash : "!";
        }
        catch
        {
            return "!";
        }
    }

    /// <summary>
    ///     Gets the UUID for a block device via blkid.
    /// </summary>
    private static string GetBlkid(string device)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "blkid",
            Arguments = $"-s UUID -o value \"{device}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null) throw new Exception("Failed to start blkid");

        var uuid = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit();

        if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(uuid))
            throw new Exception($"Failed to get UUID for {device}");

        return uuid;
    }

    /// <summary>
    ///     Gets the next available UID starting from 1000 by reading /mnt/etc/passwd.
    /// </summary>
    private int GetNextUid()
    {
        int uid = 1000;
        var passwdPath = Path.Combine(MountPoint, "etc/passwd");
        if (File.Exists(passwdPath))
        {
            foreach (var line in File.ReadAllLines(passwdPath))
            {
                var parts = line.Split(':');
                if (parts.Length >= 3 && int.TryParse(parts[2], out var existingUid))
                {
                    // Only consider UIDs in the normal user range (1000-60000).
                    // System accounts like 'nobody' (65534) must not push the
                    // first user UID past the valid login UID range.
                    if (existingUid >= uid && existingUid < 60000)
                        uid = existingUid + 1;
                }
            }
        }

        return uid;
    }

    /// <summary>
    ///     Returns a stub Lumina repo file. Replace with actual repo configuration.
    /// </summary>
    private static string GetLuminaRepoStub()
    {
        return """
              # Lumina Linux Repository
              [lumina-core]
              name=lumina 26.03 x64 - Core
              baseurl=https://packages.lumina.1t.ru/releases/26.03/x64/lumina-core
              enabled=1
              gpgcheck=0
              
              [lumina-updates]
              name=lumina 26.03 x64 - Updates
              baseurl=https://packages.lumina.1t.ru/releases/26.03/x64/lumina-updates
              enabled=1
              gpgcheck=0
              
              [lumen]
              name=lumen - core utilities
              baseurl=https://packages.lumina.1t.ru/releases/26.03/x64/lumen
              enabled=1
              gpgcheck=0
              
              [lumina-common]
              name=lumina 26.03 - Common Resources
              baseurl=https://packages.lumina.1t.ru/releases/26.03/noarch/common
              enabled=1
              gpgcheck=0
              
              [lumina-common-updates]
              name=lumina 26.03 - Common Resources (Updates)
              baseurl=https://packages.lumina.1t.ru/releases/26.03/noarch/common-updates
              enabled=1
              gpgcheck=0
              
              """;
    }
}