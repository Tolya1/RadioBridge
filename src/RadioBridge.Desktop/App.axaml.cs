using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using RadioBridge.Core;
using RadioBridge.Core.Docs;
using RadioBridge.Core.Settings;

namespace RadioBridge.Desktop;

public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private SettingsStore? _settings;
    private ILogger? _log;
    private readonly CancellationTokenSource _stop = new();
    private bool _quitting;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            var options = Program.Options;
            var loggers = Program.CreateLoggers(options.LogDirectory, console: false);
            var log = _log = loggers.CreateLogger<App>();
            log.LogInformation("RadioBridge {Version} starting on {OS}.", typeof(App).Assembly.GetName().Version, Environment.OSVersion);

            var host = new BridgeHost(options, loggers, () => Dispatcher.UIThread.Post(() => Quit("automation request")));
            var settings = _settings = host.Settings;

            // The app lives until Quit: closing the window may only hide it to the tray.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) =>
            {
                _stop.Cancel();
                host.Dispose();
                loggers.Dispose();
            };

            _window = new MainWindow
            {
                DataContext = new MainViewModel(host.Controller, settings, options.LogDirectory) { TestModeForced = options.TestMode },
            };
            _window.Closing += OnWindowClosing;
            _tray = CreateTray(settings);
            host.Controller.ViewChanged += () =>
            {
                var view = host.Controller.View;
                Dispatcher.UIThread.Post(() => _tray.ToolTipText = Tooltip(view));
            };

            bool startHidden = Program.StartMinimized || settings.Current.StartInTray;
            if (!startHidden)
                ShowWindow();
            else if (!settings.Current.CloseToTray)
            {
                _window.WindowState = WindowState.Minimized;
                _window.Show();
            }

            host.Start();
            if (StartupReader.ShouldShow(options, settings.Current, startHidden))
            {
                // After the main window is up, so the reader opens on top of it.
                Dispatcher.UIThread.Post(
                    () => _window.OpenReader(DocumentKind.Readme, dontShowAgain: () => settings.Update(s => s with { ShowReaderAtStart = false })),
                    DispatcherPriority.Background);
            }
            if (Program.OwnsInstance)
                SingleInstance.Listen(() => Dispatcher.UIThread.Post(ShowWindow), _stop.Token);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private TrayIcon CreateTray(SettingsStore settings)
    {
        var show = new NativeMenuItem("Show RadioBridge");
        show.Click += (_, _) => ShowWindow();
        var testMode = new NativeMenuItem("Test mode")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = settings.Current.TestMode,
        };
        testMode.Click += (_, _) => settings.Update(s => s with { TestMode = !s.TestMode });
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => Quit("tray menu");

        var tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://RadioBridge/Assets/radiobridge.ico"))),
            ToolTipText = "RadioBridge",
            Menu = [show, testMode, new NativeMenuItemSeparator(), quit],
            IsVisible = settings.Current.CloseToTray,
        };
        tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(this, [tray]);

        settings.Changed += s => Dispatcher.UIThread.Post(() =>
        {
            tray.IsVisible = s.CloseToTray;
            testMode.IsChecked = s.TestMode;
        });
        return tray;
    }

    private void ShowWindow()
    {
        if (_window is null)
            return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
        // Windows may refuse to let a background app take focus; a topmost blink still raises the window.
        _window.Topmost = true;
        _window.Topmost = false;
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        var closeToTray = _settings?.Current.CloseToTray == true;
        if (_quitting || !closeToTray)
        {
            Quit($"window closed ({e.CloseReason}{(e.IsProgrammatic ? ", programmatic" : "")})");
            return;
        }
        e.Cancel = true;
        _window!.Hide();
    }

    private void Quit(string reason)
    {
        if (_quitting)
            return;
        _quitting = true;
        _log?.LogInformation("Quitting: {Reason}.", reason);
        _desktop?.Shutdown();
    }

    private static string Tooltip(PanelView view) =>
        $"RadioBridge: panel {(view.PanelConnected ? "connected" : "not found")}, {(view.SimConnected ? view.SimName : "no simulator")}";
}
