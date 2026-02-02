using Aurora.Core.Logic;
using Aurora.Core.Logic.Providers;
using Spectre.Console;
using Aurora.Core.IO; 

namespace Aurora.CLI.Commands;

public class BuildCommand : ICommand
{
    public string Name => "build";
    public string Description => "Build a package from source (e.g., PKGBUILD)";

    public async Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            if (Syscall.geteuid() == 0)
            {
                DisplayRootWarning();
                // We exit immediately to prevent any further action.
                return;
            }
        }
        
        // 1. Determine build directory (default to current '.')
        string targetPath = args.Length > 0 ? Path.GetFullPath(args[0]) : Directory.GetCurrentDirectory();
        var lastLines = new Queue<string>(20);
        string buildWorkDir = Path.Combine(targetPath, ".aurora-build");
        string logFilePath = Path.Combine(buildWorkDir, "build.log");

        if (!Directory.Exists(targetPath))
        {
            throw new DirectoryNotFoundException($"Build path does not exist: {targetPath}");
        }

        AnsiConsole.Write(new Rule($"[yellow]Aurora Build Engine[/]").RuleStyle("grey"));
        AnsiConsole.MarkupLine($"[blue]Project Directory:[/] {targetPath}");
        

        // 2. Identify the Build Provider
        // In the future, we can inject a list of providers. For now, we instantiate Arch.
        var providers = new List<IBuildProvider> { new ArchBuildProvider() };
        var provider = providers.FirstOrDefault(p => p.CanHandle(targetPath));

        if (provider == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No supported build script (like PKGBUILD) found in this directory.");
            return;
        }

        AnsiConsole.MarkupLine($"[blue]Detected Format:[/] {provider.FormatName}");

        try
        {
            // 3. Phase 1: Extract Metadata (Bash Prober)
            var manifest = await provider.GetManifestAsync(targetPath);
            AnsiConsole.MarkupLine($"[green]Loaded:[/] [bold]{manifest.Package.Name}[/] v{manifest.Package.Version}");

            // 4. Phase 2: Fetch Sources
            // We use a folder inside the project for persistent source caching (SRCDEST)
            string downloadDir = Path.Combine(targetPath, "SRCDEST");
            await provider.FetchSourcesAsync(manifest, downloadDir, config.SkipGpg, config.SkipDownload, targetPath);

            // 5. Phase 3: Execute Build
            // We create a temporary build isolation folder
            
            
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Building...", async ctx => 
                {
                    Action<string> logger = (line) => 
                    {
                        var trimmed = line.Trim();
                        if (string.IsNullOrEmpty(trimmed)) return;

                        // 1. Add to the circular buffer
                        if (lastLines.Count >= 20) lastLines.Dequeue();
                        lastLines.Enqueue(trimmed);

                        // 2. Update spinner
                        var sanitized = Markup.Escape(trimmed);
                        int maxWidth = Console.WindowWidth - 20;
                        if (sanitized.Length > maxWidth && maxWidth > 0)
                            sanitized = sanitized.Substring(0, maxWidth) + "...";

                        ctx.Status($"[grey]{sanitized}[/]");
                    };

                    await provider.BuildAsync(manifest, buildWorkDir, targetPath, logger);
                });

            // 6. Cleanup (Optional)
            if (Directory.Exists(buildWorkDir))
            {
                // Directory.Delete(buildWorkDir, true);
            }

            AnsiConsole.Write(new Rule().RuleStyle("grey"));
            AnsiConsole.MarkupLine($"[bold green]Build Successful:[/] {manifest.Package.Name}-{manifest.Package.Version}.au");
        }
        catch (Exception ex)
        {
            AnsiConsole.Write(new Rule("[red]Build Failed[/]").RuleStyle("red"));
            
            // --- NEW: DISPLAY CONTEXT ON FAILURE ---
            if (lastLines.Count > 0)
            {
                var rows = string.Join(Environment.NewLine, lastLines.Select(l => "[grey]" + Markup.Escape(l) + "[/]"));
                AnsiConsole.Write(
                    new Panel(rows)
                        .Header("[yellow]Recent Log Output[/]")
                        .BorderColor(Color.Red)
                        .Expand()
                );
            }

            AnsiConsole.MarkupLine($"[red bold]Error:[/] {Markup.Escape(ex.Message)}");
            
            if (File.Exists(logFilePath))
            {
                AnsiConsole.MarkupLine($"[grey]Full log available at:[/] [blue]{logFilePath}[/]");
            }
        }
    }
    
    private void DisplayRootWarning()
    {
        AnsiConsole.Write(new FigletText("Hold Up!").Centered().Color(Color.Red));
        
        var warningText = new Markup(
            "[bold yellow]Heya![/]\n\n" +
            "You are trying to build a package as [red bold]root[/]!\n" +
            "This is a [underline red]BAAAD[/] behavior! :sheep:\n\n" +
            "Running build scripts with superuser privileges can [invert]permanently and catastrophically[/] damage your system. " +
            "A rogue script could wipe your hard drive, steal your data, or turn your computer into a toaster. " +
            "You don't want a toaster, do you?\n"
        );
        
        var footerText = new Markup(
            "[grey]Aurora's build system uses [blue]fakeroot[/] to handle file ownership correctly without needing real root access.\n" +
            "Please run this command as a regular user.[/]"
        );

        // --- THE FIX ---
        // Create a visual separator to act as the footer line
        var separator = new Rule().RuleStyle("grey");

        // Combine the main content, separator, and footer into a single renderable layout
        var panelContent = new Rows(
            warningText,
            separator,
            footerText
        );
        // -----------------

        // Pass the combined content object to the Panel constructor
        var panel = new Panel(panelContent)
            .Header("[bold red] SECURITY ALERT [/]", Justify.Center)
            .Border(BoxBorder.Double)
            .BorderColor(Color.Red)
            .Padding(2, 2);
            
        AnsiConsole.Write(panel);
    }
}