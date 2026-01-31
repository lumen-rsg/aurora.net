namespace Aurora.CLI.Commands;

public interface ICommand
{
    // Name used in CLI (e.g., "install")
    string Name { get; }
    
    // Description for Help
    string Description { get; }

    // Execution logic
    Task ExecuteAsync(CliConfiguration config, string[] args);
}