using Aurora.CLI;
using Aurora.Core.Logic;
using Aurora.Core.Logic.Providers;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class BuildCommand : ICommand
{
    public string Name => "build";
    public string Description => "Build a package from source (e.g., PKGBUILD)";

    public async Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        // 1. Determine build directory (default to current '.')
        string targetPath = args.Length > 0 ? Path.GetFullPath(args[0]) : Directory.GetCurrentDirectory();

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
            await provider.FetchSourcesAsync(manifest, downloadDir, config.SkipGpg);

            // 5. Phase 3: Execute Build
            // We create a temporary build isolation folder
            string buildWorkDir = Path.Combine(targetPath, ".aurora-build");
            
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Initializing build environment...", async ctx => 
                {
                    // Define the logger action
                    Action<string> logger = (line) => 
                    {
                        // 1. Sanitize the line (escape brackets for Spectre)
                        var sanitized = Markup.Escape(line.Trim());

                        // 2. Truncate to fit terminal width (prevents wrapping/breaking UI)
                        int maxWidth = Console.WindowWidth - 20;
                        if (sanitized.Length > maxWidth && maxWidth > 0)
                        {
                            sanitized = sanitized.Substring(0, maxWidth) + "...";
                        }

                        // 3. Update the spinner text
                        ctx.Status($"[grey]{sanitized}[/]");
                    };

                    // Trigger the build with our new logger
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
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            // Return or rethrow depending on CLI strategy
        }
    }
}