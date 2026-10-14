using System;
using Avalonia;
using AutoClicker.Platform.Linux;

namespace AutoClicker;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Macro recording launches this process as its physical input reader.
        if (args.Length > 0 && args[0] == "--input-reader") { return LibEvdevReader.Run(args); }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        // Use the application's custom window chrome on Linux/X11.
        #pragma warning disable AVALONIA_X11_CSD
        return AppBuilder.Configure<App>().UsePlatformDetect().With(new X11PlatformOptions { EnableDrawnDecorations = true }).WithInterFont();
        #pragma warning restore AVALONIA_X11_CSD
    }
}
