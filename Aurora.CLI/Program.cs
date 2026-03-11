using Aurora.CLI;
using Aurora.CLI.Commands;
using Aurora.Core.IO;
using Aurora.Core.Logging;
using Spectre.Console;

class Program
{
    static async Task<int> Main(string[] args)
    {
        AuLogger.Initialize("aurora.log");

        // 1. RPM-Native Command Registry
        var commands = new List<ICommand>
        {
            new InstallCommand(),
            new RemoveCommand(),
            new SyncCommand(),
            new UpdateCommand(),
            new ListCommand(),
            new AuditCommand(),
            new RecoverCommand(),
            new InitCommand()
        };
        
        var commandMap = commands.ToDictionary(c => c.Name, c => c);

        // 2. Global Argument Parsing
        var commandArgs = new List<string>();
        string? bootstrapPath = null;
        bool assumeYes = false;
        bool force = false;
        bool skipGpg = false;
        bool skipDownload = false; // Kept for interface compatibility, mostly unused now

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--bootstrap" || arg == "-b")
            {
                if (i + 1 < args.Length) bootstrapPath = args[++i];
                else { AnsiConsole.MarkupLine("[red]Error: --bootstrap missing path[/]"); return 1; }
            }
            else if (arg == "--force" || arg == "-f") force = true;
            else if (arg == "-y" || arg == "--yes") assumeYes = true;
            else if (arg == "--skip-gpg") skipGpg = true;
            else if (arg == "--skip-download" || arg == "-S") skipDownload = true; 
            else commandArgs.Add(arg);
        }

        // 3. Environment Setup
        string sysRoot = !string.IsNullOrEmpty(bootstrapPath) 
            ? Path.GetFullPath(bootstrapPath) 
            : (OperatingSystem.IsLinux() ? "/" : Path.Combine(Directory.GetCurrentDirectory(), "sysroot"));

        var config = new CliConfiguration(sysRoot, force, assumeYes, skipGpg, skipDownload);
        
        // Ensure core directories exist
        try 
        {
            // Only create if we are bootstrapping or if they are missing
            if (bootstrapPath != null) Directory.CreateDirectory(sysRoot);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Fatal: Could not access system root at {sysRoot}[/]");
            AuLogger.Error(ex.Message);
            return 1;
        }

        // 4. Dispatch
        if (commandArgs.Count == 0)
        {
            PrintHelp(commands);
            return 0;
        }

        var cmdName = commandArgs[0].ToLowerInvariant();
        if (commandMap.TryGetValue(cmdName, out var cmd))
        {
            try
            {
                await cmd.ExecuteAsync(config, commandArgs.Skip(1).ToArray());
                return 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.Write(new Rule("[red]FATAL ERROR[/]").RuleStyle("red"));
            
                // Unroll the exception to find the root cause
                Exception? current = ex;
                while (current != null)
                {
                    AnsiConsole.MarkupLine($"[bold red]Error Type:[/] {current.GetType().Name}");
                    AnsiConsole.MarkupLine($"[bold red]Message:[/] {Markup.Escape(current.Message)}");
                
                    if (current is System.Reflection.TargetInvocationException tie) current = tie.InnerException;
                    else if (current is TypeInitializationException tie2) current = tie2.InnerException;
                    else break;

                    if (current != null) AnsiConsole.MarkupLine("[grey]Caused by...[/]");
                }

                AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
                return 1;
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Unknown command:[/] {cmdName}");
            return 1;
        }
    }

    static void PrintHelp(List<ICommand> commands)
    {
        AnsiConsole.Write(new FigletText("aurora").Color(Color.Cyan1));
        var table = new Table().Border(TableBorder.Rounded).AddColumn("Command").AddColumn("Description");
        foreach (var c in commands) table.AddRow($"[yellow]{c.Name}[/]", c.Description);
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[grey]Flags: --bootstrap <path>, --force, --yes, --skip-gpg[/]");
    }
}