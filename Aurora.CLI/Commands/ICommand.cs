namespace Aurora.CLI.Commands;

public interface ICommand
{
    string Name { get; }
    string Description { get; }
    bool RequiresRoot => false;
    Task ExecuteAsync(CliConfiguration config, string[] args);
}