using Microsoft.Extensions.Logging;
using RadioBridge.Core.Automation;
using RadioBridge.Core.Panel;
using RadioBridge.Core.Settings;
using RadioBridge.Core.Sim;
using RadioBridge.Core.Sim.Msfs;
using RadioBridge.Core.Sim.XPlane;

namespace RadioBridge.Core;

public sealed record BridgeOptions
{
    /// <summary>Holds settings.json and the logs folder.</summary>
    public string DataDirectory { get; init; } = AppPaths.DataDirectory;

    /// <summary>Open the USB panel. Off for automated runs that must not touch the hardware.</summary>
    public bool UsePhysicalPanel { get; init; } = true;

    /// <summary>Port of the local automation API, or null to leave it off.</summary>
    public int? AutomationPort { get; init; }

    /// <summary>Force test mode on for this run, whatever the settings say.</summary>
    public bool TestMode { get; init; }

    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");
}

/// <summary>Wires the panel(s), simulators and controller together. Shared by the window and headless mode.</summary>
public sealed class BridgeHost : IDisposable
{
    private readonly ILogger _log;
    private readonly IRadioPanel _panel;
    private readonly AutomationServer? _automation;

    public BridgeHost(BridgeOptions options, ILoggerFactory loggers, Action shutdown)
    {
        _log = loggers.CreateLogger<BridgeHost>();
        Settings = new SettingsStore(options.SettingsFile, loggers.CreateLogger<SettingsStore>());

        var backends = new List<ISimBackend>();
        if (Settings.Current.MsfsEnabled)
            backends.Add(new MsfsBackend(loggers.CreateLogger<MsfsBackend>()));
        if (Settings.Current.XPlaneEnabled)
            backends.Add(new XPlaneBackend(() => Settings.Current.XPlanePort, loggers.CreateLogger<XPlaneBackend>()));
        var test = new TestSimBackend(() => options.TestMode || Settings.Current.TestMode);
        Settings.Changed += _ => test.Refresh();
        Sim = new SimRouter(backends, test, loggers.CreateLogger<SimRouter>());

        var panels = new List<IRadioPanel>();
        if (options.UsePhysicalPanel)
            panels.Add(new HidRadioPanel(loggers.CreateLogger<HidRadioPanel>()));
        VirtualRadioPanel? virtualPanel = null;
        if (options.AutomationPort is not null || panels.Count == 0)
            panels.Add(virtualPanel = new VirtualRadioPanel());
        _panel = panels.Count == 1 ? panels[0] : new PanelGroup(panels);

        Controller = new PanelController(_panel, Sim, Settings, TimeProvider.System, loggers.CreateLogger<PanelController>());

        if (options.AutomationPort is int port)
            _automation = new AutomationServer(port, Controller, virtualPanel!, Sim, Settings, shutdown, loggers.CreateLogger<AutomationServer>());
    }

    public SettingsStore Settings { get; }
    public SimRouter Sim { get; }
    public PanelController Controller { get; }

    public void Start()
    {
        Controller.Start();
        _automation?.Start();
    }

    public void Dispose()
    {
        _log.LogInformation("Shutting down.");
        _automation?.Dispose();
        Controller.Dispose();
        _panel.Show(PanelDisplays.Blank);
        _panel.Dispose();
        Sim.Dispose();
    }
}
