using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RadioBridge.Core.Automation;
using RadioBridge.Core.Diagnostics;
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

    /// <summary>
    /// Where debug recordings go. Null (the default): next to the executable that was started, where they
    /// are easy to find; <see cref="FallbackRecordingDirectory"/> if that folder can't be written.
    /// </summary>
    public string? RecordingDirectory { get; init; }

    /// <summary>Open the USB panel. Off for automated runs that must not touch the hardware.</summary>
    public bool UsePhysicalPanel { get; init; } = true;

    /// <summary>Port of the local automation API, or null to leave it off.</summary>
    public int? AutomationPort { get; init; }

    /// <summary>Force test mode on for this run, whatever the settings say.</summary>
    public bool TestMode { get; init; }

    /// <summary>Start a debug recording right away, so it covers the start too (also the RecordAtStart setting).</summary>
    public bool Record { get; init; }

    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>For recordings when the preferred folder can't be written (e.g. RadioBridge in Program Files).</summary>
    public string FallbackRecordingDirectory => Path.Combine(DataDirectory, "recordings");
}

/// <summary>Wires the panel(s), simulators and controller together. Shared by the window and headless mode.</summary>
public sealed class BridgeHost : IDisposable
{
    private readonly ILogger _log;
    private readonly BridgeOptions _options;
    private readonly IRadioPanel _panel;
    private readonly AutomationServer? _automation;

    /// <param name="recorder">The debug recording, shared with the log so log lines land in it too; one is made when null.</param>
    public BridgeHost(BridgeOptions options, ILoggerFactory loggers, Action shutdown, Recorder? recorder = null)
    {
        _options = options;
        _log = loggers.CreateLogger<BridgeHost>();
        Recorder = recorder ?? new Recorder();
        Settings = new SettingsStore(options.SettingsFile, loggers.CreateLogger<SettingsStore>());
        Settings.Changed += s => Recorder.Record("settings", "changed", w => WriteSettings(w, s));

        var backends = new List<ISimBackend>();
        if (Settings.Current.MsfsEnabled)
            backends.Add(new MsfsBackend(() => Settings.Current.SimConnectPath, log: loggers.CreateLogger<MsfsBackend>(), recorder: Recorder));
        if (Settings.Current.XPlaneEnabled)
            backends.Add(new XPlaneBackend(() => Settings.Current.XPlanePort, loggers.CreateLogger<XPlaneBackend>(), Recorder));
        var test = new TestSimBackend(() => options.TestMode || Settings.Current.TestMode);
        Settings.Changed += _ => test.Refresh();
        Sim = new SimRouter(backends, test, loggers.CreateLogger<SimRouter>(), Recorder);

        var panels = new List<IRadioPanel>();
        if (options.UsePhysicalPanel)
            panels.Add(new HidRadioPanel(loggers.CreateLogger<HidRadioPanel>(), Recorder));
        VirtualRadioPanel? virtualPanel = null;
        if (options.AutomationPort is not null || panels.Count == 0)
            panels.Add(virtualPanel = new VirtualRadioPanel());
        _panel = panels.Count == 1 ? panels[0] : new PanelGroup(panels);

        Controller = new PanelController(_panel, Sim, Settings, TimeProvider.System, loggers.CreateLogger<PanelController>(), Recorder);

        if (options.AutomationPort is int port)
            _automation = new AutomationServer(port, Controller, virtualPanel!, Sim, Settings, shutdown, loggers.CreateLogger<AutomationServer>(), this);
    }

    public SettingsStore Settings { get; }
    public SimRouter Sim { get; }
    public PanelController Controller { get; }
    public Recorder Recorder { get; }

    /// <summary>The folder of the running or last recording; before the first one, where it will go.</summary>
    public string RecordingDirectory => Recorder.FilePath is { } file ? Path.GetDirectoryName(file)! : PreferredRecordingDirectory;

    /// <summary>Where recordings go when that folder can be written: next to the executable unless set otherwise.</summary>
    public string PreferredRecordingDirectory => _options.RecordingDirectory ?? AppPaths.ExecutableDirectory;

    public void Start()
    {
        if (_options.Record || Settings.Current.RecordAtStart)
            StartRecording();
        Controller.Start();
        _automation?.Start();
    }

    /// <summary>
    /// Starts a debug recording (or keeps the running one) and returns its file: next to the executable, or in
    /// the data folder when that can't be written. Null, and a log line, when it can't be written anywhere.
    /// </summary>
    public string? StartRecording()
    {
        string? path = null;
        foreach (string folder in new[] { PreferredRecordingDirectory, _options.FallbackRecordingDirectory }.Distinct())
        {
            try
            {
                path = Recorder.Start(folder, WriteHeader);
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning("Can't write a debug recording to {Folder}: {Message}", folder, ex.Message);
            }
        }
        if (path is not null)
            _log.LogInformation("Debug recording to {Path}.", path);
        return path;
    }

    private void WriteHeader(Utf8JsonWriter w)
    {
        var app = Assembly.GetEntryAssembly() ?? typeof(BridgeHost).Assembly;
        w.WriteString("app", app.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? app.GetName().Version?.ToString());
        w.WriteString("os", RuntimeInformation.OSDescription);
        w.WriteString("runtime", RuntimeInformation.FrameworkDescription);
        w.WriteBoolean("physicalPanel", _options.UsePhysicalPanel);
        w.WriteString("executableDirectory", AppPaths.ExecutableDirectory);
        w.WriteString("dataDirectory", _options.DataDirectory);
        WriteSettings(w, Settings.Current);
        w.WriteString("simulator", Sim.Name);
        w.WriteBoolean("panelConnected", Controller.View.PanelConnected);
        w.WriteRadioState("radios", Sim.State);
    }

    public void StopRecording()
    {
        string? path = Recorder.FilePath;
        bool wasRecording = Recorder.IsRecording;
        Recorder.Stop();
        if (wasRecording)
            _log.LogInformation("Debug recording stopped: {Path} ({Bytes} bytes).", path, Recorder.BytesWritten);
    }

    private static void WriteSettings(Utf8JsonWriter w, BridgeSettings settings)
    {
        w.WritePropertyName("settings");
        JsonSerializer.SerializeToElement(settings, AutomationServer.Json).WriteTo(w);
    }

    public void Dispose()
    {
        _log.LogInformation("Shutting down.");
        _automation?.Dispose();
        Controller.Dispose();
        _panel.Show(PanelDisplays.Blank);
        _panel.Dispose();
        Sim.Dispose();
        StopRecording();
    }
}
