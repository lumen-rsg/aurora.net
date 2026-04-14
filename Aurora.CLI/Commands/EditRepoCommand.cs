using System.Diagnostics;
using Aurora.Core.IO;
using Aurora.Core.Logging;
using Aurora.Core.Parsing;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Aurora.CLI.Commands;

public class EditRepoCommand : ICommand
{
    public string Name => "edit";
    public string Description => "Edit, add, or remove repositories (yum.repos.d)";

    public Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        var reposDir = PathHelper.GetPath(config.SysRoot, "etc/yum.repos.d");

        if (!Directory.Exists(reposDir))
        {
            AuLogger.Warn($"Edit: repository directory not found: {reposDir}");
            AnsiConsole.MarkupLine("[red]Error:[/] Repository directory not found: " + reposDir);
            AnsiConsole.MarkupLine("[grey]Run 'aurora init' first to create the directory structure.[/]");
            return Task.CompletedTask;
        }

        // Parse flags
        bool modeAdd = false;
        bool modeRemove = false;
        bool modeList = false;
        string? removeTarget = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--add":
                    modeAdd = true;
                    break;
                case "--remove":
                    modeRemove = true;
                    if (i + 1 < args.Length) removeTarget = args[++i];
                    break;
                case "--list":
                    modeList = true;
                    break;
            }
        }

        if (modeList)
            return ListRepos(reposDir);
        if (modeAdd)
            return AddRepo(reposDir);
        if (modeRemove)
            return RemoveRepo(reposDir, removeTarget);

        // Default: interactive mode
        return InteractiveEdit(reposDir);
    }

    // ─── Non-Interactive Modes ───────────────────────────────────────────

    private Task ListRepos(string reposDir)
    {
        var repos = RepoConfigParser.ParseDirectory(reposDir);
        if (repos.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No repositories configured.[/]");
            return Task.CompletedTask;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("ID")
            .AddColumn("Name")
            .AddColumn("Base URL")
            .AddColumn("Enabled")
            .AddColumn("GPG Check")
            .AddColumn("Source File");

        foreach (var repo in repos.Values.OrderBy(r => r.Id))
        {
            var enabled = repo.Enabled ? "[green]✔ Yes[/]" : "[red]✘ No[/]";
            var gpg = repo.GpgCheck ? "[green]✔ Yes[/]" : "[grey]✘ No[/]";
            var source = repo.SourceFile != null ? Path.GetFileName(repo.SourceFile) : "-";
            var url = string.IsNullOrEmpty(repo.BaseUrl) ? "-" : Markup.Escape(repo.BaseUrl);

            table.AddRow(
                Markup.Escape(repo.Id),
                Markup.Escape(repo.Name),
                url,
                enabled,
                gpg,
                Markup.Escape(source)
            );
        }

        AnsiConsole.Write(table);
        return Task.CompletedTask;
    }

    private Task AddRepo(string reposDir)
    {
        AnsiConsole.MarkupLine("[bold cyan]➕ Add New Repository[/]");
        AnsiConsole.WriteLine();

        var id = AnsiConsole.Ask<string>("Repository [yellow]ID[/] (e.g. my-custom-repo):");
        if (string.IsNullOrWhiteSpace(id))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Repository ID cannot be empty.");
            return Task.CompletedTask;
        }

        var name = AnsiConsole.Ask<string>("Repository [yellow]Name[/] (e.g. My Custom Packages):");
        var baseUrl = AnsiConsole.Ask<string>("Repository [yellow]Base URL[/]:");
        var gpgCheck = AnsiConsole.Confirm("Enable [yellow]GPG Check[/]?", false);

        string? gpgKey = null;
        if (gpgCheck)
        {
            gpgKey = AnsiConsole.Ask<string>("[yellow]GPG Key URL[/] (leave empty to skip):");
            if (string.IsNullOrWhiteSpace(gpgKey)) gpgKey = null;
        }

        var enabled = AnsiConsole.Confirm("Enable repository?", true);

        var repo = new Aurora.Core.Contract.RepoConfig
        {
            Id = id.Trim(),
            Name = name.Trim(),
            BaseUrl = baseUrl.Trim(),
            GpgCheck = gpgCheck,
            GpgKey = gpgKey ?? string.Empty,
            Enabled = enabled,
            SourceFile = Path.Combine(reposDir, "aurora-custom.repo")
        };

        // Append to existing file or create new
        var targetFile = repo.SourceFile;
        var content = RepoConfigParser.Serialize(repo);
        if (File.Exists(targetFile))
            File.AppendAllText(targetFile, "\n" + content);
        else
            File.WriteAllText(targetFile, content);

        AuLogger.Info($"Edit: repository '{repo.Id}' added to {Path.GetFileName(targetFile)}.");
        AnsiConsole.MarkupLine($"[green bold]✔ Repository '{Markup.Escape(repo.Id)}' added to {Path.GetFileName(targetFile)}[/]");
        return Task.CompletedTask;
    }

    private Task RemoveRepo(string reposDir, string? repoId)
    {
        if (string.IsNullOrWhiteSpace(repoId))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Specify a repository ID. Usage: aurora edit --remove <repo-id>");
            return Task.CompletedTask;
        }

        var repos = RepoConfigParser.ParseDirectory(reposDir);
        if (!repos.ContainsKey(repoId))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Repository '{Markup.Escape(repoId)}' not found.");
            AnsiConsole.MarkupLine("[grey]Use 'aurora edit --list' to see available repositories.[/]");
            return Task.CompletedTask;
        }

        var repo = repos[repoId];
        if (!AnsiConsole.Confirm($"Remove repository [yellow]{Markup.Escape(repoId)}[/] ({Markup.Escape(repo.Name)})?", false))
        {
            AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
            return Task.CompletedTask;
        }

        RepoConfigParser.RemoveRepo(repos, repoId);
        AuLogger.Info($"Edit: repository '{repoId}' removed.");
        AnsiConsole.MarkupLine($"[green bold]✔ Repository '{Markup.Escape(repoId)}' removed.[/]");
        return Task.CompletedTask;
    }

    // ─── Interactive Mode ────────────────────────────────────────────────

    private Task InteractiveEdit(string reposDir)
    {
        while (true)
        {
            var repos = RepoConfigParser.ParseDirectory(reposDir);

            if (repos.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No repositories configured.[/]");
                if (AnsiConsole.Confirm("Add a new repository?", true))
                    AddRepo(reposDir);
                return Task.CompletedTask;
            }

            // Build main menu choices
            var choices = new List<string>();
            foreach (var repo in repos.Values.OrderBy(r => r.Id))
            {
                var badge = repo.Enabled ? "[green]●[/]" : "[red]○[/]";
                choices.Add($"{badge} {Markup.Escape(repo.Id)} — {Markup.Escape(repo.Name)}");
            }
            choices.Add("[cyan]➕ Add New Repository[/]");
            choices.Add("[grey]↩ Quit[/]");

            AnsiConsole.WriteLine();
            var selection = new SelectionPrompt<string>()
                .Title("[bold cyan]📦 Repository Editor[/] [grey](j/k or ↑↓ to navigate, Enter to select, q to quit)[/]")
                .PageSize(15)
                .HighlightStyle(new Style(foreground: Color.Cyan1, background: Color.Grey11))
                .AddChoices(choices);

            // VIM key hint in the prompt
            var chosen = AnsiConsole.Prompt(selection);

            if (chosen.Contains("Quit") || chosen.Contains("↩"))
                return Task.CompletedTask;

            if (chosen.Contains("Add New Repository"))
            {
                AddRepo(reposDir);
                continue;
            }

            // Extract repo ID from the choice string: "● repo-id — Name"
            var repoId = ExtractId(chosen);
            if (repoId == null) continue;

            var result = RepoSubMenu(reposDir, repos[repoId]);
            if (result == SubMenuResult.QuitApp)
                return Task.CompletedTask;
        }
    }

    private SubMenuResult RepoSubMenu(string reposDir, Aurora.Core.Contract.RepoConfig repo)
    {
        while (true)
        {
            AnsiConsole.WriteLine();
            // Show repo details
            var panel = new Panel(
                new Rows(
                    new Markup($"[bold]ID:[/]         {Markup.Escape(repo.Id)}"),
                    new Markup($"[bold]Name:[/]       {Markup.Escape(repo.Name)}"),
                    new Markup($"[bold]Base URL:[/]   {Markup.Escape(repo.BaseUrl)}"),
                    new Markup($"[bold]Enabled:[/]    {(repo.Enabled ? "[green]Yes[/]" : "[red]No[/]")}"),
                    new Markup($"[bold]GPG Check:[/]  {(repo.GpgCheck ? "[green]Yes[/]" : "[grey]No[/]")}"),
                    string.IsNullOrEmpty(repo.GpgKey)
                        ? new Markup("[bold]GPG Key:[/]    [grey]Not set[/]")
                        : new Markup($"[bold]GPG Key:[/]    {Markup.Escape(repo.GpgKey)}"),
                    new Markup($"[bold]File:[/]       {Markup.Escape(repo.SourceFile ?? "unknown")}")
                ))
            {
                Header = new PanelHeader($"[bold cyan]{Markup.Escape(repo.Id)}[/]"),
                Border = BoxBorder.Rounded
            };
            AnsiConsole.Write(panel);

            var actions = new List<string>
            {
                "✏️  Edit fields",
                repo.Enabled ? "🔘 Disable" : "🔘 Enable",
                "🗑️  Remove repository",
                "📝 Open in $EDITOR",
                "↩ Back to list",
            };

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Choose action:[/]")
                    .PageSize(10)
                    .HighlightStyle(new Style(foreground: Color.Cyan1, background: Color.Grey11))
                    .AddChoices(actions)
            );

            if (choice.Contains("Back to list"))
                return SubMenuResult.BackToList;

            if (choice.Contains("Edit fields"))
            {
                EditRepoFields(reposDir, repo);
                return SubMenuResult.BackToList; // Refresh
            }

            if (choice.Contains("Disable") || choice.Contains("Enable"))
            {
                repo.Enabled = !repo.Enabled;
                RepoConfigParser.SaveAllRepos(
                    RepoConfigParser.ParseDirectory(reposDir)
                        .ToDictionary(k => k.Key, v => v.Value)
                );
                // Update just this repo's enabled state
                var allRepos = RepoConfigParser.ParseDirectory(reposDir);
                if (allRepos.TryGetValue(repo.Id, out var updated))
                    repo.Enabled = updated.Enabled;

                AnsiConsole.MarkupLine($"[green]✔ Toggled {Markup.Escape(repo.Id)} → {(repo.Enabled ? "enabled" : "disabled")}[/]");
                continue;
            }

            if (choice.Contains("Remove"))
            {
                if (AnsiConsole.Confirm($"Delete [red]{Markup.Escape(repo.Id)}[/]?", false))
                {
                    var repos = RepoConfigParser.ParseDirectory(reposDir);
                    RepoConfigParser.RemoveRepo(repos, repo.Id);
                    AnsiConsole.MarkupLine($"[green bold]✔ Removed {Markup.Escape(repo.Id)}[/]");
                    return SubMenuResult.BackToList;
                }
                continue;
            }

            if (choice.Contains("Open in $EDITOR"))
            {
                OpenInEditor(repo.SourceFile);
                return SubMenuResult.BackToList; // Refresh after editor
            }
        }
    }

    private void EditRepoFields(string reposDir, Aurora.Core.Contract.RepoConfig repo)
    {
        AnsiConsole.MarkupLine($"[cyan]Editing {Markup.Escape(repo.Id)}[/] [grey](press Enter to keep current value)[/]");
        AnsiConsole.WriteLine();

        var newName = AnsiConsole.Prompt(
            new TextPrompt<string>($"[yellow]Name[/] [{Markup.Escape(repo.Name)}]:")
                .AllowEmpty()
                .DefaultValue(repo.Name!)
                .ShowDefaultValue(false));

        var newUrl = AnsiConsole.Prompt(
            new TextPrompt<string>($"[yellow]Base URL[/] [{Markup.Escape(repo.BaseUrl)}]:")
                .AllowEmpty()
                .DefaultValue(repo.BaseUrl!)
                .ShowDefaultValue(false));

        var newGpgKey = AnsiConsole.Prompt(
            new TextPrompt<string>($"[yellow]GPG Key[/] [{(string.IsNullOrEmpty(repo.GpgKey) ? "none" : Markup.Escape(repo.GpgKey))}]:")
                .AllowEmpty()
                .DefaultValue(repo.GpgKey ?? string.Empty)
                .ShowDefaultValue(false));

        var newGpgCheck = AnsiConsole.Confirm($"[yellow]GPG Check[/]? (currently {(repo.GpgCheck ? "yes" : "no")})", repo.GpgCheck);

        // Apply changes
        repo.Name = string.IsNullOrWhiteSpace(newName) ? repo.Name : newName.Trim();
        repo.BaseUrl = string.IsNullOrWhiteSpace(newUrl) ? repo.BaseUrl : newUrl.Trim();
        repo.GpgKey = string.IsNullOrWhiteSpace(newGpgKey) ? repo.GpgKey : newGpgKey.Trim();
        repo.GpgCheck = newGpgCheck;

        // Reload all repos, update this one, save
        var allRepos = RepoConfigParser.ParseDirectory(reposDir);
        if (allRepos.TryGetValue(repo.Id, out var existing))
        {
            existing.Name = repo.Name;
            existing.BaseUrl = repo.BaseUrl;
            existing.GpgKey = repo.GpgKey;
            existing.GpgCheck = repo.GpgCheck;
        }
        RepoConfigParser.SaveAllRepos(allRepos);

        AuLogger.Info($"Edit: updated repository '{repo.Id}'.");
        AnsiConsole.MarkupLine($"[green bold]✔ Updated {Markup.Escape(repo.Id)}[/]");
    }

    private void OpenInEditor(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Source file not found.");
            return;
        }

        var editor = Environment.GetEnvironmentVariable("EDITOR")
                     ?? Environment.GetEnvironmentVariable("VISUAL")
                     ?? "vi";  // Default to vi/vim

        AnsiConsole.MarkupLine($"[grey]Opening {Path.GetFileName(filePath)} in {editor}...[/]");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = editor,
                Arguments = $"\"{filePath}\"",
                UseShellExecute = false
            };

            var proc = Process.Start(psi);
            proc?.WaitForExit();

            if (proc?.ExitCode == 0)
                AnsiConsole.MarkupLine("[green]✔ Editor closed successfully.[/]");
            else
                AnsiConsole.MarkupLine("[yellow]Editor exited with non-zero code. Changes may not have been saved.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to launch editor:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.MarkupLine("[grey]Set $EDITOR or $VISUAL environment variable to your preferred editor.[/]");
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static string? ExtractId(string choice)
    {
        // Format: "● repo-id — Name" or "○ repo-id — Name"
        // or with markup: "[green]●[/] repo-id — Name"
        var clean = choice
            .Replace("[green]", "").Replace("[/]", "")
            .Replace("[red]", "").Replace("[cyan]", "")
            .Replace("[grey]", "")
            .Trim();

        // Find the em-dash separator
        var dashIndex = clean.IndexOf('—');
        if (dashIndex > 0)
        {
            // Extract the part between the bullet and the dash
            var segment = clean.Substring(0, dashIndex).Trim();
            // Remove the bullet character
            return segment.TrimStart('●', '○', ' ').Trim();
        }

        return clean.TrimStart('●', '○', ' ').Trim();
    }

    private enum SubMenuResult
    {
        BackToList,
        QuitApp
    }
}