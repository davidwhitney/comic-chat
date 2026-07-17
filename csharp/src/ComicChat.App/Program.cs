using Avalonia;
using System;

namespace ComicChat.App;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Headless mode: render a scripted comic to a PNG and exit. Useful for verifying the
        // engine end-to-end without a display.
        if (args.Length >= 1 && args[0] == "--render")
        {
            var outPath = args.Length >= 2 ? args[1] : "comic.png";
            BuildAvaloniaApp().SetupWithoutStarting();
            ComicRenderer.RenderToPng(outPath);
            return;
        }

        // Drives the history subsystem end-to-end: replay, .ccc round-trip, and resize.
        if (args.Length >= 1 && args[0] == "--verify-history")
        {
            var outDir = args.Length >= 2 ? args[1] : "verify-out";
            BuildAvaloniaApp().SetupWithoutStarting();
            Environment.ExitCode = ComicRenderer.VerifyHistory(outDir);
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
