using Aurora.CLI;
using Aurora.CLI.Commands;
using Aurora.Core.IO;
using Aurora.Core.Logging;
using Aurora.Core.Logic;
using Spectre.Console;

class Program
{
    static async Task<int> Main(string[] args)
    {
        AuLogger.Initialize("aurora.log");

        // 1. Production Command Registry
        var commands = new List<ICommand>
        {
            new InstallCommand(),
            new RemoveCommand(),
            new SyncCommand(),
            new UpdateCommand(),
            new ListCommand(),
            new AuditCommand(),
            new RecoverCommand(),
            new BuildCommand(),
            new EditCommand(),
            new ConvertCommand()
        };
        
        var commandMap = commands.ToDictionary(c => c.Name, c => c);

        // 2. Global Argument Parsing
        var commandArgs = new List<string>();
        string? bootstrapPath = null;
        bool assumeYes = false;
        bool force = false;
        bool skipGpg = false;
        bool skipDownload = false;

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
        
        // Ensure the internal state directory exists
        try 
        {
            Directory.CreateDirectory(Path.GetDirectoryName(config.DbPath)!);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Fatal: Could not initialize system root at {sysRoot}[/]");
            AuLogger.Error(ex.Message);
            return 1;
        }

        // 4. Strict Recovery Check
        if (Transaction.HasPendingRecovery(config.DbPath))
        {
            bool isRecoverCmd = commandArgs.Count > 0 && commandArgs[0] == "recover";
            if (!assumeYes && !isRecoverCmd)
            {
                AnsiConsole.MarkupLine("[red bold]![/] [yellow]Previous transaction was interrupted abnormally.[/]");
                if (Console.IsInputRedirected || !AnsiConsole.Confirm("Recover system state now?"))
                {
                     AnsiConsole.MarkupLine("[red]Cannot proceed with a dirty journal. Run 'au recover' manually.[/]");
                     return 1;
                }
            }
            
            if (!isRecoverCmd) 
            {
                try { 
                    Transaction.RunRecovery(config.DbPath); 
                    AnsiConsole.MarkupLine("[green]System recovered.[/]"); 
                }
                catch (Exception ex) { 
                    AnsiConsole.MarkupLine($"[red]Recovery failed:[/] {ex.Message}"); 
                    return 1; 
                }
            }
        }

        // 5. Dispatch
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
                AnsiConsole.MarkupLine($"[red bold]Error:[/] {Markup.Escape(ex.Message)}");
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
        AnsiConsole.Write(new FigletText("AURORA").Color(Color.Cyan1));
        var table = new Table().Border(TableBorder.Rounded).AddColumn("Command").AddColumn("Description");
        foreach (var c in commands) table.AddRow($"[yellow]{c.Name}[/]", c.Description);
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[grey]Flags: --bootstrap <path>, --force, --yes[/]");
    }
}