using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using RadioBridge.Core;

namespace RadioBridge.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var options = Program.Options;
            var loggers = Program.CreateLoggers(options.LogDirectory, console: false);
            var log = loggers.CreateLogger<App>();
            log.LogInformation("RadioBridge {Version} starting on {OS}.", typeof(App).Assembly.GetName().Version, Environment.OSVersion);

            var host = new BridgeHost(options, loggers, () => Dispatcher.UIThread.Post(() => desktop.Shutdown()));

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(host.Controller, host.Settings, options.LogDirectory),
                WindowState = Program.StartMinimized ? WindowState.Minimized : WindowState.Normal,
            };
            desktop.Exit += (_, _) =>
            {
                host.Dispose();
                loggers.Dispose();
            };

            host.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
