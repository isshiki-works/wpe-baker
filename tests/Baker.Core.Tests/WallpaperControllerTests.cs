using Baker.Core;
using Xunit;

[Trait("Layer", "L1")]
public class WallpaperControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlreadyCancelledControlRequestStopsBeforeCheckingOrStartingWallpaperEngine(bool query)
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var controller = new WallpaperController(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "wallpaper64.exe"));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query
            ? controller.GetWallpaperAsync("Monitor0", cancelled.Token)
            : controller.CloseWindowAsync("cancelled-private-preview", cancelled.Token));
        Assert.Equal(cancelled.Token, error.CancellationToken);
    }
}
