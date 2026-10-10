using Avalonia;
using Microsoft.Extensions.Logging;
using RadioBridge.Core;
using Serilog;

namespace RadioBridge.Desktop;

internal static class Program
{
    private const string Usage = """
        RadioBridge: Logitech/Saitek Pro Flight Radio Panel for MSFS and X-Plane.

        Options:
          --headless           Run without a window (stop with Ctrl+C).
          --minimized          Start with the window minimized.
          --no-panel           Do not open the USB panel.
          --automation PORT    Enable the local automation API (testing only).
          --data-dir DIR       Folder for settings.json and logs.
          --help               Show this text.
        """;

    public static BridgeOptions Options { get; private set; } = new();
    public static bool StartMinimized { get; private set; }

    // Don't use Avalonia, third-party APIs or SynchronizationContext-reliant code before AppMain is
    // called: things aren't initialized yet.
    [STAThread]
    public static int Main(string[] args)
    {
        bool headless;
        try
        {
            (Options, headless, StartMinimized) = ParseArgs(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        // Two instances would fight over the panel's displays.
        using var instance = new Mutex(initiallyOwned: true, "RadioBridge.SingleInstance", out bool first);
        if (!first && Options.UsePhysicalPanel)
        {
            Console.Error.WriteLine("RadioBridge is already running.");
            return 1;
        }

        return headless ? RunHeadless() : BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static int RunHeadless()
    {
        using var loggers = CreateLoggers(Options.LogDirectory, console: true);
        using var stop = new ManualResetEventSlim();
        using (var host = new BridgeHost(Options, loggers, stop.Set))
        {
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.Set();
            };
            host.Start();
            stop.Wait();
        }
        return 0;
    }

    public static ILoggerFactory CreateLoggers(string logDirectory, bool console)
    {
        var config = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(logDirectory, "radiobridge-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
        if (console)
            config = config.WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
        var serilog = config.CreateLogger();
        return LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddSerilog(serilog, dispose: true));
    }

    private static (BridgeOptions Options, bool Headless, bool Minimized) ParseArgs(string[] args)
    {
        var options = new BridgeOptions();
        bool headless = false, minimized = false;
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--headless": headless = true; break;
                case "--minimized": minimized = true; break;
                case "--no-panel": options = options with { UsePhysicalPanel = false }; break;
                case "--automation":
                    options = options with
                    {
                        AutomationPort = int.TryParse(Next(), out int port) && port is > 0 and < 65536
                            ? port
                            : throw new ArgumentException("--automation needs a port number."),
                    };
                    break;
                case "--data-dir": options = options with { DataDirectory = Path.GetFullPath(Next()) }; break;
                case "--help" or "-h" or "/?": throw new ArgumentException("");
                default: throw new ArgumentException($"Unknown option {args[i]}.");
            }
        }
        return (options, headless, minimized);
    }

    // Avalonia configuration, don't remove; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
