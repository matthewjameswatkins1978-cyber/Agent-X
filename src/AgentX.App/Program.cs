using Avalonia;

namespace AgentX.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            // Missing-glyph fallback for the bundled Latin feed faces (arrows,
            // box drawing, CJK): see AxFontFallback for the ordered chain.
            .With(AxFontFallback.Options)
            .LogToTrace();
}