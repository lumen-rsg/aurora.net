using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Aurora.Core.IO;
using Aurora.Core.Logic.Build;
using Aurora.Core.Models;
using Aurora.Core.Security;
using Spectre.Console;

namespace Aurora.CLI.Commands;

public class EditCommand : ICommand
{
    public string Name => "edit";
    public string Description => "Interactively edit a PKGBUILD file";

    private enum FieldCategory { Identity, Metadata, Dependencies, Relations, Security, Files }

    private record EditableField(string Name, string Description, bool IsArray, FieldCategory Category);

    private readonly List<EditableField> _fields = new()
    {
        new("pkgname", "Package name", true, FieldCategory.Identity),
        new("pkgver", "Version (e.g. 1.0.0)", false, FieldCategory.Identity),
        new("pkgrel", "Release number", false, FieldCategory.Identity),
        new("epoch", "Epoch (forced versioning)", false, FieldCategory.Identity),
        new("pkgdesc", "Short description", false, FieldCategory.Metadata),
        new("url", "Upstream URL", false, FieldCategory.Metadata),
        new("arch", "Architecture", true, FieldCategory.Metadata),
        new("license", "Software license", true, FieldCategory.Metadata),
        new("groups", "Package groups", true, FieldCategory.Metadata),
        new("PACKAGER", "Maintainer override", false, FieldCategory.Metadata),
        new("depends", "Runtime dependencies", true, FieldCategory.Dependencies),
        new("makedepends", "Build-time dependencies", true, FieldCategory.Dependencies),
        new("checkdepends", "Test-time dependencies", true, FieldCategory.Dependencies),
        new("optdepends", "Optional dependencies", true, FieldCategory.Dependencies),
        new("conflicts", "Conflicting packages", true, FieldCategory.Relations),
        new("provides", "Virtual provisions", true, FieldCategory.Relations),
        new("replaces", "Replaced packages", true, FieldCategory.Relations),
        new("validpgpkeys", "Trusted GPG Fingerprints", true, FieldCategory.Security),
        new("sha256sums", "SHA256 Checksums", true, FieldCategory.Security),
        new("source", "Source URLs/Files", true, FieldCategory.Files),
        new("backup", "Config files to preserve", true, FieldCategory.Files),
        new("install", "Install script (.INSTALL)", false, FieldCategory.Files),
        new("changelog", "Changelog file", false, FieldCategory.Files)
    };

    public async Task ExecuteAsync(CliConfiguration config, string[] args)
    {
        var pkgbuildPath = Path.Combine(Directory.GetCurrentDirectory(), "PKGBUILD");
        if (!File.Exists(pkgbuildPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No PKGBUILD found in the current directory.");
            return;
        }

        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new Rule("[yellow]Aurora PKGBUILD Editor[/]").RuleStyle("grey"));

            var lines = File.ReadAllLines(pkgbuildPath);

            var prompt = new SelectionPrompt<string>()
                .Title("Select a field to edit:")
                .PageSize(15)
                .MoreChoicesText("[grey](Move up and down to reveal more fields)[/]");

            var groups = _fields.GroupBy(f => f.Category);
            foreach (var group in groups)
            {
                prompt.AddChoiceGroup($"[bold blue]{group.Key}[/]", group.Select(f => f.Name));
            }

            prompt.AddChoiceGroup("[bold yellow]Advanced[/]", new[] { 
                "Regenerate Checksums", 
                "Edit Header Comments", 
                "Open in $EDITOR" 
            });
            
            prompt.AddChoice("[bold red]Exit[/]");

            var choice = AnsiConsole.Prompt(prompt);

            if (choice == "[bold red]Exit[/]") break;

            switch (choice)
            {
                case "Open in $EDITOR":
                    EditorHelper.OpenFileInEditor(pkgbuildPath);
                    continue;
                case "Regenerate Checksums":
                    await RegenerateChecksums(pkgbuildPath);
                    continue;
                case "Edit Header Comments":
                    EditComments(pkgbuildPath);
                    continue;
            }

            var field = _fields.First(f => f.Name == choice);
            await HandleFieldEdit(pkgbuildPath, lines, field);
        }
    }

    private async Task HandleFieldEdit(string path, string[] lines, EditableField field)
    {
        var current = GetCurrentValue(lines, field);
        
        AnsiConsole.MarkupLine($"Editing [green]{field.Name}[/]: {field.Description}");
        if (!string.IsNullOrEmpty(current)) 
        {
            // FIX: Escape current value to prevent bracket errors
            AnsiConsole.MarkupLine($"[grey]Current:[/] [italic]{Markup.Escape(current)}[/]");
        }
        
        var newValue = AnsiConsole.Prompt(
            new TextPrompt<string>("Enter new value:")
                .DefaultValue(current)
                .AllowEmpty());

        ApplyChanges(path, field.Name, newValue, field.IsArray);
        AnsiConsole.MarkupLine("[green]✔ Field updated.[/] [grey]Press any key...[/]");
        Console.ReadKey(true);
    }

    private string GetCurrentValue(string[] lines, EditableField field)
    {
        var pattern = $@"^\s*{field.Name}=\((.*?)\)";
        if (!field.IsArray) pattern = $@"^\s*{field.Name}=['""]?(.*?)['""]?$";

        var combined = string.Join("\n", lines);
        // Handle multi-line arrays by allowing . to match newlines in regex
        var match = Regex.Match(combined, pattern, RegexOptions.Singleline | RegexOptions.Multiline);

        if (match.Success)
        {
            var val = match.Groups[1].Value.Trim();
            // Clean up bash-style quotes and whitespace for the UI
            return val.Replace("'", "").Replace("\"", "").Replace("\n", " ").Replace("\r", "");
        }

        return string.Empty;
    }

    private void ApplyChanges(string path, string fieldName, string newValue, bool isArray)
    {
        var lines = File.ReadAllLines(path).ToList();
        string replacement;

        if (isArray)
        {
            var items = newValue.Split(new[] { ' ', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(i => $"'{i.Trim('\'', '\"', ' ')}'");
            replacement = $"{fieldName}=({string.Join(" ", items)})";
        }
        else
        {
            replacement = $"{fieldName}='{newValue.Trim('\'', '\"')}'";
        }

        int index = lines.FindIndex(l => Regex.IsMatch(l, $@"^\s*{fieldName}="));

        if (index != -1)
        {
            // If it's a multi-line array, we must remove all lines until the closing parenthesis
            if (lines[index].Contains("(") && !lines[index].Contains(")"))
            {
                int endIdx = index;
                while (endIdx < lines.Count && !lines[endIdx].Contains(")"))
                {
                    endIdx++;
                }
                int countToRemove = (endIdx - index) + 1;
                lines.RemoveRange(index, countToRemove);
            }
            else
            {
                lines.RemoveAt(index);
            }
            lines.Insert(index, replacement);
        }
        else
        {
            lines.Add(replacement);
        }

        File.WriteAllLines(path, lines);
    }

    private void EditComments(string path)
    {
        var lines = File.ReadAllLines(path).ToList();
        var head = lines.TakeWhile(l => l.TrimStart().StartsWith("#") || string.IsNullOrWhiteSpace(l)).ToList();
        var rest = lines.Skip(head.Count).ToList();

        // 1. Create a temporary file
        string tempFile = Path.Combine(Path.GetTempPath(), $"AURORA_HEAD_{Guid.NewGuid()}.txt");
        File.WriteAllLines(tempFile, head);

        // 2. Open in user's editor
        AnsiConsole.MarkupLine("[yellow]Opening header in $EDITOR...[/]");
        EditorHelper.OpenFileInEditor(tempFile);

        // 3. Read back
        var newHead = File.ReadAllLines(tempFile);

        // 4. Combine and Save
        var final = new List<string>();
        final.AddRange(newHead);
        final.AddRange(rest);
        File.WriteAllLines(path, final);

        // 5. Cleanup
        if (File.Exists(tempFile)) File.Delete(tempFile);

        AnsiConsole.MarkupLine("[green]✔ Header comments updated via editor.[/]");
        Console.ReadKey(true);
    }

    private async Task RegenerateChecksums(string path)
    {
        await AnsiConsole.Status().StartAsync("Parsing sources and hashing files...", async ctx =>
        {
            var lines = File.ReadAllLines(path);
            var sourceVal = GetCurrentValue(lines, _fields.First(f => f.Name == "source"));
            
            if (string.IsNullOrEmpty(sourceVal)) return;

            var sourceItems = sourceVal.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var newHashes = new List<string>();

            foreach (var s in sourceItems)
            {
                var entry = new SourceEntry(s);
                var localFile = entry.FileName;
                var srcDestFile = Path.Combine("SRCDEST", entry.FileName);

                string fileToHash = File.Exists(localFile) ? localFile : 
                    File.Exists(srcDestFile) ? srcDestFile : null;

                if (fileToHash != null)
                {
                    // FIX: Use Markup.Escape to protect filenames from Spectre tags
                    ctx.Status($"[grey]Hashing {Markup.Escape(entry.FileName)}...[/]");
                    newHashes.Add(HashHelper.ComputeFileHash(fileToHash));
                }
                else
                {
                    newHashes.Add("SKIP");
                }
            }

            ApplyChanges(path, "sha256sums", string.Join(" ", newHashes), true);
        });

        AnsiConsole.MarkupLine("[green]✔ sha256sums updated.[/]");
        AnsiConsole.MarkupLine("[grey]Press any key...[/]");
        Console.ReadKey(true);
    }
}