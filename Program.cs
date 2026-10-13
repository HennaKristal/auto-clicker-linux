using System;
using Avalonia;

namespace AutoClicker;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        // Use the application's custom window chrome on Linux/X11.
        #pragma warning disable AVALONIA_X11_CSD
        var builder = AppBuilder.Configure<App>().UsePlatformDetect().With(new X11PlatformOptions { EnableDrawnDecorations = true }).WithInterFont().LogToTrace();
        #pragma warning restore AVALONIA_X11_CSD

        #if DEBUG
        builder.WithDeveloperTools();
        #endif

        return builder;
    }
}
