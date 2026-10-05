using System.IO;
using System.Text.Json.Nodes;
using Baker.Core;

namespace Baker.App;

public partial class MainWindow
{
    private CancellationTokenSource? previewCancellation;

    private async Task RefreshScenePreviewAsync()
    {
        previewCancellation?.Cancel();
        CurrentWallpaperCard.DataContext = null;
        if (!IsLoaded || tools is null || !AppEnvironment.SourceExists(SourceBox.Text.Trim()) ||
            !AppEnvironment.AssetsValid(AssetsBox.Text.Trim())) return;
        using var pending = new CancellationTokenSource();
        previewCancellation = pending;
        CurrentWallpaperItem? item = null;
        try
        {
            string source = SourceBox.Text.Trim(), assets = AssetsBox.Text.Trim();
            using var project = new ProjectSource(source);
            if (project.Kind != "scene") return;
            string title = Path.GetFileName(project.DirectoryPath);
            var cover = (CurrentWallpaperBox.SelectedItem as CurrentWallpaperItem)?.Preview;
            string metadata = Path.Combine(project.DirectoryPath, "project.json");
            if (File.Exists(metadata))
            {
                JsonObject? information = JsonNode.Parse(await File.ReadAllTextAsync(metadata, pending.Token)) as JsonObject;
                title = information?["title"]?.GetValue<string>() ?? title;
                if (cover is null && information?["preview"]?.GetValue<string>() is string preview)
                    cover = LoadPreview(Path.Combine(project.DirectoryPath, preview));
            }
            pending.Token.ThrowIfCancellationRequested();
            item = new(title, source, source, title, cover);
            CurrentWallpaperCard.DataContext = item;
            JsonObject properties = AppJsonPresentation.MergeWpeProperties(sourceWpeProperties,
                sourcePropertyDefinitions, analysisPreviewOverrides).Properties;
            var dimensions = OutputResolution.PrimaryDisplay() ?? (1920u, 1080u);
            string? device = (GpuBox.SelectedItem as VulkanDeviceInfo)?.DeviceUuid;
            // Source/asset editors may fire in quick succession; only the final selection renders.
            await Task.Delay(150, pending.Token);
            var image = await Task.Run(() => ScenePreview.RenderAsync(tools, source, assets,
                dimensions.Item1, dimensions.Item2, device, properties, pending.Token), pending.Token);
            if (!pending.IsCancellationRequested && ReferenceEquals(CurrentWallpaperCard.DataContext, item))
                item.Preview = image;
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
        catch (Exception error)
        {
            // An unsupported preview must not prevent importing or analyzing the wallpaper.
            if (item is not null && ReferenceEquals(CurrentWallpaperCard.DataContext, item))
                item.PreviewError = error.Message;
        }
        finally { if (ReferenceEquals(previewCancellation, pending)) previewCancellation = null; }
    }
}
