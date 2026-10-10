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
    private readonly SettingsStore _settings;
    private PanelView _view;

    public MainViewModel(PanelController controller, SettingsStore settings, string logDirectory)
    {
        _settings = settings;
        LogPath = logDirectory;
        _view = controller.View;
        controller.ViewChanged += () =>
        {
            var view = controller.View;
            Dispatcher.UIThread.Post(() => ShowView(view));
        };
        settings.Changed += _ => Dispatcher.UIThread.Post(() => Raise(null));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ---- live panel ----

    public bool PanelConnected => _view.PanelConnected;
    public string PanelStatus => _view.PanelConnected ? "Panel connected" : "Panel not found";
    public bool SimConnected => _view.SimConnected;
    public string SimStatus => _view.SimConnected ? _view.SimName : "No simulator";
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

    public bool BaroInHg
    {
        get => S.BaroUnit == BaroUnit.InchesOfMercury;
        set => Update(s => s with { BaroUnit = value ? BaroUnit.InchesOfMercury : BaroUnit.Hectopascal });
    }

    public bool AccelerationEnabled
    {
        get => S.Acceleration.Enabled;
        set => Update(s => s with { Acceleration = s.Acceleration with { Enabled = value } });
    }

    public decimal? FastDetentMs
    {
        get => S.Acceleration.FastDetentMs;
        set => Update(s => s with { Acceleration = s.Acceleration with { FastDetentMs = ToInt(value, 35) } });
    }

    public decimal? FastMultiplier
    {
        get => S.Acceleration.FastMultiplier;
        set => Update(s => s with { Acceleration = s.Acceleration with { FastMultiplier = ToInt(value, 8) } });
    }

    public decimal? MediumDetentMs
    {
        get => S.Acceleration.MediumDetentMs;
        set => Update(s => s with { Acceleration = s.Acceleration with { MediumDetentMs = ToInt(value, 90) } });
    }

    public decimal? MediumMultiplier
    {
        get => S.Acceleration.MediumMultiplier;
        set => Update(s => s with { Acceleration = s.Acceleration with { MediumMultiplier = ToInt(value, 3) } });
    }

    public decimal? LongPressMs
    {
        get => S.LongPressMs;
        set => Update(s => s with { LongPressMs = ToInt(value, 600) });
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
