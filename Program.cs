using Avalonia;
using System;

namespace AutoClicker;

class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()

    .UsePlatformDetect()

    #pragma warning disable AVALONIA_X11_CSD
    .With(new X11PlatformOptions { EnableDrawnDecorations = true })
    #pragma warning restore AVALONIA_X11_CSD

    #if DEBUG
    .WithDeveloperTools()
    #endif

    .WithInterFont()
    .LogToTrace();
}
