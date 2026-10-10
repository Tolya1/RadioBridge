using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using RadioBridge.Core;
using RadioBridge.Core.Panel;
using RadioBridge.Core.Radio;
using RadioBridge.Core.Settings;

namespace RadioBridge.Desktop;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly BridgeHost _host;
    private readonly SettingsStore _settings;
    private readonly DispatcherTimer _recordingTimer;
    private PanelView _view;

    public MainViewModel(BridgeHost host, string logDirectory)
    {
        _host = host;
        _settings = host.Settings;
        LogPath = logDirectory;
        var controller = host.Controller;
        _view = controller.View;
        controller.ViewChanged += () =>
        {
            var view = controller.View;
            Dispatcher.UIThread.Post(() => ShowView(view));
        };
        _settings.Changed += _ => Dispatcher.UIThread.Post(() => Raise(null));

        // The file grows while recording: refresh its size now and then.
        _recordingTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Raise(nameof(RecordingStatus)));
        host.Recorder.Changed += () => Dispatcher.UIThread.Post(OnRecordingChanged);
        OnRecordingChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ---- live panel ----

    public bool PanelConnected => _view.PanelConnected;
    public string PanelStatus => _view.PanelConnected ? "Panel connected" : "Panel not found";
    public bool SimConnected => _view.SimConnected;
    public string SimStatus => _view.SimConnected ? _view.SimName : "No simulator";

    // What keeps a simulator from connecting, when the user can do something about it.
    public bool HasNotice => _view.Notice is not null;
    public string Notice => _view.Notice?.Text ?? "";
    public bool CanLocateSimConnect => _view.Notice?.CanLocateSimConnect == true;
    public string UpperMode => ModeText(_view.Upper);
    public string LowerMode => ModeText(_view.Lower);
    public string UpperLeft => _view.Displays.UpperLeft;
    public string UpperRight => _view.Displays.UpperRight;
    public string LowerLeft => _view.Displays.LowerLeft;
    public string LowerRight => _view.Displays.LowerRight;

    public int UpperInnerTurns => _view.UpperActivity.InnerTurns;
    public int UpperOuterTurns => _view.UpperActivity.OuterTurns;
    public bool UpperPressed => _view.UpperActivity.ButtonDown;
    public bool UpperHeld => _view.UpperActivity.LongPress;
    public int LowerInnerTurns => _view.LowerActivity.InnerTurns;
    public int LowerOuterTurns => _view.LowerActivity.OuterTurns;
    public bool LowerPressed => _view.LowerActivity.ButtonDown;
    public bool LowerHeld => _view.LowerActivity.LongPress;

    // ---- settings ----

    private BridgeSettings S => _settings.Current;

    public bool Spacing833
    {
        get => S.ComSpacing == ComSpacing.Khz833;
        set => Update(s => s with { ComSpacing = value ? ComSpacing.Khz833 : ComSpacing.Khz25 });
    }

    public bool CompactCom
    {
        get => S.CompactComDisplay;
        set => Update(s => s with { CompactComDisplay = value });
    }

    // Radio-button pairs: each one only sets its own value; the other button being cleared is a no-op.
    public bool BaroHpa
    {
        get => S.BaroUnit == BaroUnit.Hectopascal;
        set { if (value) Update(s => s with { BaroUnit = BaroUnit.Hectopascal }); }
    }

    public bool BaroInHg
    {
        get => S.BaroUnit == BaroUnit.InchesOfMercury;
        set { if (value) Update(s => s with { BaroUnit = BaroUnit.InchesOfMercury }); }
    }

    public bool AccelerationEnabled
    {
        get => S.ComAcceleration.Enabled;
        set => Update(s => s with { ComAcceleration = s.ComAcceleration with { Enabled = value } });
    }

    public decimal? FastDetentMs
    {
        get => S.ComAcceleration.FastDetentMs;
        set => Update(s => s with { ComAcceleration = s.ComAcceleration with { FastDetentMs = ToInt(value, 20) } });
    }

    public decimal? FastMultiplier
    {
        get => S.ComAcceleration.FastMultiplier;
        set => Update(s => s with { ComAcceleration = s.ComAcceleration with { FastMultiplier = ToInt(value, 3) } });
    }

    public decimal? MediumDetentMs
    {
        get => S.ComAcceleration.MediumDetentMs;
        set => Update(s => s with { ComAcceleration = s.ComAcceleration with { MediumDetentMs = ToInt(value, 40) } });
    }

    public decimal? MediumMultiplier
    {
        get => S.ComAcceleration.MediumMultiplier;
        set => Update(s => s with { ComAcceleration = s.ComAcceleration with { MediumMultiplier = ToInt(value, 2) } });
    }

    public decimal? LongPressMs
    {
        get => S.LongPressMs;
        set => Update(s => s with { LongPressMs = ToInt(value, 600) });
    }

    public bool Vfr7000
    {
        get => S.VfrSquawk == 7000;
        set { if (value) Update(s => s with { VfrSquawk = 7000 }); }
    }

    public bool Vfr1200
    {
        get => S.VfrSquawk == 1200;
        set { if (value) Update(s => s with { VfrSquawk = 1200 }); }
    }

    public bool TestMode
    {
        get => TestModeForced || S.TestMode;
        set
        {
            if (!TestModeForced)
                Update(s => s with { TestMode = value });
        }
    }

    /// <summary>Test mode was switched on from the command line, so the setting can't turn it off.</summary>
    public bool TestModeForced { get; init; }

    public bool ShowReaderAtStart
    {
        get => S.ShowReaderAtStart;
        set => Update(s => s with { ShowReaderAtStart = value });
    }

    public string VersionText => $"RadioBridge {Reader.AppInfo.VersionText}";

    public bool CloseToTray
    {
        get => S.CloseToTray;
        set => Update(s => s with { CloseToTray = value });
    }

    public bool StartInTray
    {
        get => S.StartInTray;
        set => Update(s => s with { StartInTray = value });
    }

    public bool HasSimConnectPath => !string.IsNullOrWhiteSpace(S.SimConnectPath);
    public string SimConnectLibrary => HasSimConnectPath ? S.SimConnectPath! : "found automatically";

    /// <summary>Uses this SimConnect library for MSFS, or searches for one again when null.</summary>
    public void SetSimConnectPath(string? path) => Update(s => s with { SimConnectPath = path });

    public bool MsfsEnabled
    {
        get => S.MsfsEnabled;
        set => Update(s => s with { MsfsEnabled = value });
    }

    public bool XPlaneEnabled
    {
        get => S.XPlaneEnabled;
        set => Update(s => s with { XPlaneEnabled = value });
    }

    public decimal? XPlanePort
    {
        get => S.XPlanePort;
        set => Update(s => s with { XPlanePort = ToInt(value, 8086) });
    }

    public string SettingsPath => _settings.FilePath;
    public string LogPath { get; }

    // ---- debug recording ----

    public bool IsRecording => _host.Recorder.IsRecording;

    public string RecordButtonText => IsRecording ? "Stop recording" : "Record debug log";

    public string RecordingStatus
    {
        get
        {
            var recorder = _host.Recorder;
            string? file = recorder.FilePath is { } path ? Path.GetFileName(path) : null;
            if (recorder.IsRecording)
                return $"Recording to {file} ({Size(recorder.BytesWritten)}). Reproduce the problem, then stop.";
            return file is null
                ? "Saves everything the panel, RadioBridge and the simulator do to a file you can send with a bug report."
                : $"Saved {file} ({Size(recorder.BytesWritten)}).";
        }
    }

    public string RecordingDirectory => _host.RecordingDirectory;

    public void ToggleRecording()
    {
        if (IsRecording)
            _host.StopRecording();
        else
            _host.StartRecording();
    }

    private void OnRecordingChanged()
    {
        _recordingTimer.IsEnabled = IsRecording;
        Raise(nameof(IsRecording));
        Raise(nameof(RecordButtonText));
        Raise(nameof(RecordingStatus));
    }

    private static string Size(long bytes) => bytes < 1024 * 1024
        ? $"{Math.Max(1, bytes / 1024)} KB"
        : $"{bytes / (1024.0 * 1024):0.0} MB";

    private void ShowView(PanelView view)
    {
        _view = view;
        Raise(null);
    }

    private void Update(Func<BridgeSettings, BridgeSettings> change) => _settings.Update(change);

    private static int ToInt(decimal? value, int fallback) => value is { } v ? (int)v : fallback;

    private static string ModeText(Selector? selector) => selector switch
    {
        null => "—",
        Selector.Com1 => "COM1",
        Selector.Com2 => "COM2",
        Selector.Nav1 => "NAV1",
        Selector.Nav2 => "NAV2",
        Selector.Adf => "ADF",
        Selector.Dme => "DME",
        _ => "XPDR",
    };

    // null = every property, which is cheap for a view this small.
    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
