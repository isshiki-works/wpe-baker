using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Baker.App;

public static class App
{
    [STAThread]
    public static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception error) when (args.Length >= 2 && args[0] == "--layout-qa")
        {
            string output = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "qa-error.txt"), error.ToString());
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        // 上一次分析留在 %TEMP%\WpeBaker 里的工作目录没人删过（中途关窗口的尤其），启动时顺手清掉过期的。
        Baker.Core.NativeEnvironment.PruneAnalysisScratch();
        var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var window = new MainWindow();
        if (args.Length >= 2 && args[0] == "--layout-qa")
        {
            // Developer verification only: no Show, window handle, input or shell launch.
            string output = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(output);
            // 参数：--layout-qa 输出目录 来源 assets [已完成的 bake.json] [analyze 出来的 plan.json]
            // 后两个都可以传空串跳过；带上 plan.json 才能截到分析完之后的结论区。
            if (args.Length >= 4) window.PrepareLayoutQa(args[2], args[3],
                args.Length >= 5 && args[4].Length > 0 ? args[4] : null,
                args.Length >= 6 && args[5].Length > 0 ? args[5] : null);
            // 截图要把左边一整栏都收进来，QA 模式下临时把窗口拉高；正常运行的窗口尺寸不受影响。
            window.Height = 1340;
            foreach (bool english in new[] { false, true })
            {
                window.SetLanguage(english);
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(window.Width, window.Height));
                content.Arrange(new Rect(0, 0, window.Width, window.Height));
                content.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
                var background = new DrawingVisual();
                using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, window.Width, window.Height));
                bitmap.Render(background);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = new FileStream(Path.Combine(output, english ? "window-en.png" : "window-zh.png"), FileMode.CreateNew);
                encoder.Save(file);
            }
            application.Shutdown();
            return 0;
        }
        return application.Run(window);
    }
}
