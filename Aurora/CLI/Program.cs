using System.Security.AccessControl;
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

        // 1. Register Commands
        var commands = new List<ICommand>
        {
            new InstallCommand(),
            new RemoveCommand(),
            new SyncCommand(),
            new UpdateCommand(),
            new ListCommand(),
            new InitCommand(),
            new AuditCommand(),
            new RecoverCommand(),
            new GenRepoCommand(),
            new TestGenCommand()
        };
        
        var commandMap = commands.ToDictionary(c => c.Name, c => c);

        // 2. Parse Globals
        var commandArgs = new List<string>();
        string? bootstrapPath = null;
        bool assumeYes = false;
        bool force = false;
        bool skipSig = false;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--bootstrap" || args[i] == "-b")
            {
                if (i + 1 < args.Length) { bootstrapPath = args[++i]; }
                else { AnsiConsole.MarkupLine("[red]Error: --bootstrap missing path[/]"); return 1; }
            }
            else if (args[i] == "--force") force = true;
            else if (args[i] == "-y" || args[i] == "--yes" || args[i] == "--noconfirm") assumeYes = true;
            else if (args[i] == "--skip-sig") skipSig = true;
            else commandArgs.Add(args[i]);
        }

        // 3. Build Configuration
        string sysRoot = !string.IsNullOrEmpty(bootstrapPath) 
            ? Path.GetFullPath(bootstrapPath) 
            : (OperatingSystem.IsLinux() ? "/" : Path.Combine(Directory.GetCurrentDirectory(), "sysroot"));

        if (!string.IsNullOrEmpty(bootstrapPath)) 
            AnsiConsole.MarkupLine($"[yellow bold]BOOTSTRAP MODE:[/] Installing to [blue]{sysRoot}[/]");
        else if (!OperatingSystem.IsLinux())
            AnsiConsole.MarkupLine($"[grey]Dev Mode (Non-Linux): Defaulting root to {sysRoot}[/]");

        var config = new CliConfiguration(sysRoot, force, assumeYes);
        Directory.CreateDirectory(Path.GetDirectoryName(config.DbPath)!);

        // 4. Recovery Check
        if (Transaction.HasPendingRecovery(config.DbPath))
        {
            bool isRecoverCmd = commandArgs.Count > 0 && commandArgs[0] == "recover";
            if (!assumeYes && !isRecoverCmd)
            {
                AnsiConsole.MarkupLine("[red bold]![/] [yellow]Previous transaction was interrupted.[/]");
                if (Console.IsInputRedirected || !AnsiConsole.Confirm("Recover system state?"))
                {
                     AnsiConsole.MarkupLine("[red]Cannot proceed with pending recovery. Exiting.[/]");
                     return 1;
                }
            }
            if (!isRecoverCmd) // If it IS recover cmd, let the RecoverCommand handle it to avoid duplicate work
            {
                try { Transaction.RunRecovery(config.DbPath); AnsiConsole.MarkupLine("[green]System recovered.[/]"); }
                catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Recovery failed:[/] {ex.Message}"); return 1; }
            }
        }

        // 5. Execute Command
        if (commandArgs.Count == 0)
        {
            PrintHelp(commands);
            return 0;
        }

        var cmdName = commandArgs[0];
        if (commandMap.TryGetValue(cmdName, out var cmd))
        {
            try
            {
                // Pass arguments excluding the command name itself
                await cmd.ExecuteAsync(config, commandArgs.Skip(1).ToArray());
                return 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red bold]FATAL ERROR:[/] {Markup.Escape(ex.Message)}");
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
        AnsiConsole.WriteLine("");
        AnsiConsole.Write(
            """
            .d8888b. dP    dP 88d888b. .d8888b. 88d888b. .d8888b.
            88'  `88 88    88 88'  `88 88'  `88 88'  `88 88'  `88 
            88.  .88 88.  .88 88       88.  .88 88       88.  .88 
            `88888P8 `88888P' dP       `88888P' dP       `88888P8 

            aurora package manager - epoch V - dotnet 10 - lumina
            """
        );
        AnsiConsole.WriteLine("");
        AnsiConsole.WriteLine("");
        
        var table = new Table().AddColumn("Command").AddColumn("Description");
        foreach (var c in commands) table.AddRow(c.Name, c.Description);
        table.AddRow("au --bootstrap <path>", "Run in bootstrap mode");
        AnsiConsole.Write(table);
    }
}