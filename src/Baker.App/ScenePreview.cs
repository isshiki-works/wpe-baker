using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Baker.Core;

namespace Baker.App;

internal static class ScenePreview
{
    internal static async Task<BitmapSource> RenderAsync(NativeTools tools, string source, string assets,
        uint width, uint height, string? device, JsonObject properties, CancellationToken token)
    {
        string output = Path.Combine(Path.GetTempPath(), "WpeBaker", "analysis-preview-" + Guid.NewGuid().ToString("N"));
        try
        {
            JsonObject result = await new NativeRenderRunner(tools).RenderRawAsync(new(source, assets, output,
                width, height, 60, 1, Frames: 1, Seed: 17, UserProperties: properties, DeviceUuid: device,
                EpochMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), token);
            byte[] pixels = await File.ReadAllBytesAsync(result["rgba_path"]!.GetValue<string>(), token);
            // Native frames are RGBA; WPF's 32-bit bitmap format is BGRA.
            for (int i = 0; i < pixels.Length; i += 4)
                (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            token.ThrowIfCancellationRequested();
            BitmapSource image = BitmapSource.Create(checked((int)width), checked((int)height), 96, 96,
                PixelFormats.Bgra32, null, pixels, checked((int)width * 4));
            image.Freeze();
            return image;
        }
        finally
        {
            // The runner exclusively owns this new GUID directory; the image is already in memory.
            try { if (Directory.Exists(output)) Directory.Delete(output, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
