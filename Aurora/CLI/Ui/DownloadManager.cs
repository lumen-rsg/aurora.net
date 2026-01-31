// File: Aurora.CLI/Ui/DownloadManager.cs
using Spectre.Console;
using Aurora.Core.Logging;
using Spectre.Console.Rendering;

namespace Aurora.CLI.Ui;

public class DownloadTask
{
    public string PackageName { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public bool SimulateFailure { get; set; } 
}

public static class DownloadManager
{
    public static async Task ProcessDownloadsAsync(List<DownloadTask> downloads)
    {
        AuLogger.Info($"Starting batch download of {downloads.Count} items.");
        AnsiConsole.MarkupLine("[bold yellow][[transaction]] : retrieving assets...[/]");

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(new ProgressColumn[] 
            {
                new TaskDescriptionColumn(),    
                new SpinnerColumn(Spinner.Known.Dots), 
                new ProgressBarColumn(),        
                new PercentageColumn(),         
                new DownloadStatusColumn() 
            })
            .StartAsync(async ctx =>
            {
                var tasks = new List<Task>();

                foreach (var item in downloads)
                {
                    var progressTask = ctx.AddTask($"[blue]{item.PackageName}[/]");
                    
                    // Initialize state
                    progressTask.State.Update<ItemStatus>("status", _ => ItemStatus.Downloading);

                    tasks.Add(SimulateDownload(progressTask, item));
                }

                await Task.WhenAll(tasks);
            });
            
        AnsiConsole.WriteLine(); 
    }

    private static async Task SimulateDownload(ProgressTask task, DownloadTask info)
    {
        var random = new Random();
        
        while (!task.IsFinished)
        {
            await Task.Delay(random.Next(50, 150));
            
            if (info.SimulateFailure && task.Value >= 95)
            {
                task.State.Update<ItemStatus>("status", _ => ItemStatus.Failed);
                task.StopTask();
                AuLogger.Error($"Download failed: {info.PackageName}");
                return;
            }

            task.Increment(random.Next(5, 15));

            if (task.Value >= 100)
            {
                task.Value = 100;
                task.State.Update<ItemStatus>("status", _ => ItemStatus.Success);
                task.StopTask();
                AuLogger.Info($"Download finished: {info.PackageName}");
                return;
            }
        }
    }
}

public class DownloadStatusColumn : ProgressColumn
{
    public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
    {
        var status = task.State.Get<ItemStatus>("status");

        if (status == ItemStatus.Success)
            return new Markup("[green]✓[/]");
        
        if (status == ItemStatus.Failed)
            return new Markup("[red]⨉ [[FAILED]][/]");

        return new Text("");
    }
}