using System.Runtime.InteropServices;
using Avalonia;
using Microsoft.Extensions.Logging;
using RadioBridge.Core;
using RadioBridge.Core.Diagnostics;
using RadioBridge.Core.Docs;
using RadioBridge.Core.Panel;
using RadioBridge.Desktop.Reader;
using Serilog;

namespace RadioBridge.Desktop;

internal static partial class Program
{
    private const string Usage = """
        RadioBridge: Logitech/Saitek Pro Flight Radio Panel for MSFS and X-Plane.

        Options:
          --headless           Run without a window (stop with Ctrl+C).
          --minimized          Start hidden in the tray (or minimized when the tray option is off).
          --no-panel           Do not open the USB panel.
          --test-mode          Drive the panel with built-in test radios while no simulator is connected.
          --record             Start a debug recording right away (see "Reporting a problem" in the readme).
          --panel-test         Check the panel: show test patterns, then print knob and button events.
          --automation PORT    Enable the local automation API (testing only).
          --data-dir DIR       Folder for settings.json, logs and debug recordings.
          --readme             Print the readme that is built into this executable.
          --licenses           Print the license and the third-party notices built into this executable.
          --help               Show this text.
        """;

    private enum Mode { Window, Headless, PanelTest, Readme, Licenses, Help }

    public static BridgeOptions Options { get; private set; } = new();
    public static bool StartMinimized { get; private set; }

    /// <summary>This is the main instance, so later launches should be routed to its window.</summary>
    public static bool OwnsInstance { get; private set; }

    /// <summary>
    /// This build's version (major.minor.patch), which decides who makes way when another RadioBridge runs. Tests
    /// set another with RADIOBRIDGE_TEST_VERSION, together with <see cref="SingleInstance.TestInstanceVariable"/>.
    /// </summary>
    public static Version AppVersion { get; } =
        (Environment.GetEnvironmentVariable(SingleInstance.TestInstanceVariable) is { Length: > 0 }
            ? SingleInstance.ParseVersion(Environment.GetEnvironmentVariable("RADIOBRIDGE_TEST_VERSION"))
            : null)
        ?? SingleInstance.Normalize(typeof(Program).Assembly.GetName().Version ?? new Version(0, 0));

    /// <summary>The older RadioBridge this launch replaced, for the log; null when there was none.</summary>
    public static string? Replaced { get; private set; }

    // Don't use Avalonia, third-party APIs or SynchronizationContext-reliant code before AppMain is
    // called: things aren't initialized yet.
    [STAThread]
    public static int Main(string[] args)
    {
        Mode mode;
        try
        {
            (Options, mode, StartMinimized) = ParseArgs(args);
        }
        catch (ArgumentException ex)
        {
            AttachParentConsole();
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (mode != Mode.Window)
            AttachParentConsole();
        if (mode == Mode.Help)
        {
            Console.WriteLine(Usage);
            return 0;
        }
        if (mode is Mode.Readme or Mode.Licenses)
        {
            // Plain reads of the built-in documents: no panel, no instance check, works with another RadioBridge running.
            PrintDocuments(mode == Mode.Readme ? [DocumentKind.Readme] : [DocumentKind.License, DocumentKind.ThirdPartyNotices]);
            return 0;
        }

        // One RadioBridge per user session, whatever the mode. The only exception is a test run that
        // neither opens the panel nor serves anyone (--automation with --no-panel): it may run beside
        // the real one, and stays out of the single-instance hand-over so it never catches a launch.
        bool testRun = Options.AutomationPort is not null && !Options.UsePhysicalPanel;
        bool first = true;
        using var instance = testRun ? null : new Mutex(initiallyOwned: true, SingleInstance.MutexName, out first);
        if (!first && TakeOver(instance!, mode) is int exitCode)
            return exitCode;
        OwnsInstance = instance is not null;

        try
        {
            return mode switch
            {
                Mode.Headless => RunHeadless(),
                Mode.PanelTest => RunPanelTest(),
                _ => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args),
            };
        }
        finally
        {
            // Let a newer launch that is waiting for its turn in right away.
            if (OwnsInstance)
                instance!.ReleaseMutex();
        }
    }

    /// <summary>
    /// Another RadioBridge runs. An older one makes way (a current one is asked to quit, one of 0.1.4 or older
    /// is stopped) and this launch takes over; otherwise the running one shows its window and this launch ends.
    /// </summary>
    /// <returns>Null when this launch now owns <paramref name="instance"/> and should run; otherwise its exit code.</returns>
    private static int? TakeOver(Mutex instance, Mode mode)
    {
        bool show = mode == Mode.Window;
        var reply = SingleInstance.AskRunning(AppVersion, show);

        string? older = null;
        if (reply.Answer == LaunchAnswer.Quitting)
            older = reply.Running?.ToString() ?? "older";
        else if (reply.Answer == LaunchAnswer.Legacy && SingleInstance.StopOlderLegacy(AppVersion) is { Count: > 0 } stopped)
            older = string.Join(", ", stopped);

        if (older is not null)
        {
            if (WaitForTurn(instance))
            {
                Replaced = older;
                return null;
            }
            Console.Error.WriteLine($"RadioBridge {older} is running and didn't make way for this newer one.");
            return 1;
        }

        bool shown = show && (reply.Answer == LaunchAnswer.Staying
                              || (reply.Answer == LaunchAnswer.Legacy && SingleInstance.AskLegacyToShow()));
        if (shown)
        {
            Console.WriteLine("RadioBridge is already running; its window was brought to the front.");
            return 0;
        }
        Console.Error.WriteLine("RadioBridge is already running.");
        return 1;
    }

    /// <summary>Waits until the RadioBridge that is making way has gone.</summary>
    private static bool WaitForTurn(Mutex instance)
    {
        try
        {
            return instance.WaitOne(TimeSpan.FromSeconds(15));
        }
        catch (AbandonedMutexException)
        {
            return true; // it was stopped without letting go: the mutex is ours now
        }
    }

    private static void PrintDocuments(DocumentKind[] documents)
    {
        // The texts contain non-ASCII names (a copyright line names "AvaloniaUI OÜ"); a console's default code
        // page would garble them.
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        for (int i = 0; i < documents.Length; i++)
        {
            if (i > 0)
                Console.Write("\n\n----------------------------------------------------------------------\n\n");
            Console.Write(DocumentLibrary.Read(documents[i]));
        }
        Console.Write('\n'); // the texts are LF only; so is the output, to the end
    }

    private static int RunHeadless()
    {
        var recorder = new Recorder();
        using var loggers = CreateLoggers(Options.LogDirectory, console: true, recorder);
        using var stop = new ManualResetEventSlim();
        var log = loggers.CreateLogger("RadioBridge.Headless");
        if (Replaced is { } replaced)
            log.LogInformation("Took over from RadioBridge {Older}, which made way for this newer version.", replaced);
        using var listening = new CancellationTokenSource();
        using (var host = new BridgeHost(Options, loggers, stop.Set, recorder))
        {
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.Set();
            };
            if (OwnsInstance)
            {
                SingleInstance.Listen(AppVersion, show: () => { }, quitForNewer: newer =>
                {
                    log.LogInformation("Quitting: RadioBridge {Newer} is starting.", newer);
                    stop.Set();
                }, listening.Token);
            }
            host.Start();
            stop.Wait();
            listening.Cancel();
        }
        return 0;
    }

    /// <summary>Hardware self-check: patterns on the displays, then live input events.</summary>
    private static int RunPanelTest()
    {
        using var loggers = CreateLoggers(Options.LogDirectory, console: false);
        using var panel = new HidRadioPanel(loggers.CreateLogger<HidRadioPanel>());
        using var connected = new ManualResetEventSlim();
        panel.ConnectionChanged += c => { if (c) connected.Set(); };
        panel.Input += e => Console.WriteLine($"  {e}");
        panel.Start();

        Console.WriteLine("Looking for the radio panel...");
        if (!connected.Wait(TimeSpan.FromSeconds(10)))
        {
            Console.Error.WriteLine("No radio panel found (USB 06A3:0D05). On Linux, check the udev rule.");
            return 1;
        }
        Console.WriteLine("Connected. Selector positions read from the panel:");
        Thread.Sleep(300);

        var patterns = new (string Expect, PanelDisplays Displays)[]
        {
            ("each display shows its own number:  11111  22222 / 33333  44444", new("11111", "22222", "33333", "44444")),
            ("every decimal point lit:  1.2.3.4.5. on all four", new("1.2.3.4.5.", "1.2.3.4.5.", "1.2.3.4.5.", "1.2.3.4.5.")),
            ("dashes on all four", new("-----", "-----", "-----", "-----")),
            ("radio style:  23.405  21.505 / 110.30  113.50", new("23.405", "21.505", "110.30", "113.50")),
        };
        foreach (var (expect, displays) in patterns)
        {
            Console.WriteLine($"Displays should show {expect}");
            panel.Show(displays);
            Thread.Sleep(3000);
        }
        panel.Show(PanelDisplays.Blank);

        Console.WriteLine("Displays blanked. Now turn knobs, move selectors and press ACT/STBY for 20 seconds:");
        Thread.Sleep(20_000);
        Console.WriteLine("Done.");
        return 0;
    }

    /// <param name="recorder">Log lines are copied into its debug recordings.</param>
    public static ILoggerFactory CreateLoggers(string logDirectory, bool console, Recorder? recorder = null)
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
        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug).AddSerilog(serilog, dispose: true);
            if (recorder is not null)
                builder.AddProvider(new RecorderLoggerProvider(recorder));
        });
    }

    private static (BridgeOptions Options, Mode Mode, bool Minimized) ParseArgs(string[] args)
    {
        var options = new BridgeOptions();
        var mode = Mode.Window;
        bool minimized = false;
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--headless": mode = Mode.Headless; break;
                case "--panel-test": mode = Mode.PanelTest; break;
                case "--readme": mode = Mode.Readme; break;
                case "--licenses": mode = Mode.Licenses; break;
                case "--minimized": minimized = true; break;
                case "--no-panel": options = options with { UsePhysicalPanel = false }; break;
                case "--test-mode": options = options with { TestMode = true }; break;
                case "--record": options = options with { Record = true }; break;
                case "--automation":
                    options = options with
                    {
                        AutomationPort = int.TryParse(Next(), out int port) && port is > 0 and < 65536
                            ? port
                            : throw new ArgumentException("--automation needs a port number."),
                    };
                    break;
                case "--data-dir":
                    // A data folder given explicitly collects everything, recordings too (test runs rely on it).
                    string dataDirectory = Path.GetFullPath(Next());
                    options = options with { DataDirectory = dataDirectory, RecordingDirectory = Path.Combine(dataDirectory, "recordings") };
                    break;
                case "--help" or "-h" or "/?": mode = Mode.Help; break;
                default: throw new ArgumentException($"Unknown option {args[i]}.");
            }
        }
        return (options, mode, minimized);
    }

    /// <summary>
    /// On Windows the app is a GUI program, so it has no console of its own. For the console modes,
    /// write to the terminal it was started from (unless output is redirected anyway).
    /// </summary>
    private static void AttachParentConsole()
    {
        if (OperatingSystem.IsWindows() && !Console.IsOutputRedirected)
            AttachConsole(-1);
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

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
