using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RadioBridge.Core.Panel;
using RadioBridge.Core.Radio;
using RadioBridge.Core.Settings;
using RadioBridge.Core.Sim;

namespace RadioBridge.Core;

/// <summary>What the UI shows: connection state, selector positions, the four displays and live input.</summary>
public sealed record PanelView(
    bool PanelConnected,
    bool SimConnected,
    string SimName,
    Selector? Upper,
    Selector? Lower,
    PanelDisplays Displays,
    RowActivity UpperActivity,
    RowActivity LowerActivity);

/// <summary>Live input on one row, so the UI can show that the controls react (with or without a sim).</summary>
/// <param name="InnerTurns">Net inner-knob detents since start, clockwise positive.</param>
/// <param name="OuterTurns">Net outer-knob detents since start, clockwise positive.</param>
/// <param name="ButtonDown">ACT/STBY is held.</param>
/// <param name="LongPress">ACT/STBY has been held long enough to count as a long press.</param>
public sealed record RowActivity(int InnerTurns, int OuterTurns, bool ButtonDown, bool LongPress)
{
    public static readonly RowActivity None = new(0, 0, false, false);
}

/// <summary>
/// Decides what the panel does: maps knobs and buttons to sim commands and renders the displays.
/// <para>
/// Values the knobs change are shown immediately and kept for a short hold-off, so slightly older
/// values still on their way back from the sim cannot make the display jump back or eat detents
/// during a fast spin. Once the sim reports the same value, or the hold-off ends, the sim is
/// authoritative again (so a radio that snaps a frequency is shown truthfully).
/// </para>
/// </summary>
public sealed class PanelController : IDisposable
{
    private static readonly TimeSpan HoldOff = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan FlashTime = TimeSpan.FromSeconds(1.2);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(100);

    private const int AdfMinKhz = 190;
    private const int AdfMaxKhz = 1799;
    private const double BaroMinHpa = 900;
    private const double BaroMaxHpa = 1100;

    /// <summary>Values the panel has set and is waiting for the sim to confirm.</summary>
    private enum Field { Com1, Com2, Nav1, Nav2, Adf1, Adf2, Squawk, Baro }

    private readonly IRadioPanel _panel;
    private readonly ISimBackend _sim;
    private readonly SettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly KnobAccelerator _accelerator;
    private readonly object _gate = new();

    private readonly Selector?[] _selectors = new Selector?[2];
    private readonly bool[] _editBaro = new bool[2];
    private readonly ITimer?[] _longPressTimers = new ITimer?[2];
    private readonly bool[] _longPressFired = new bool[2];
    private readonly (int Count, long Last)[] _presses = new (int, long)[2]; // quick presses in a row, per row
    private readonly (string Left, string Right, long Until)?[] _flash = new (string, string, long)?[2];
    private readonly Dictionary<Field, (double Value, long Until)> _pending = new();
    private readonly RowActivity[] _activity = [RowActivity.None, RowActivity.None];
    private ITimer? _refresh;

    public PanelController(IRadioPanel panel, ISimBackend sim, SettingsStore settings, TimeProvider? time = null, ILogger<PanelController>? log = null)
    {
        _panel = panel;
        _sim = sim;
        _settings = settings;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger<PanelController>.Instance;
        _accelerator = new KnobAccelerator(_time);
        View = new PanelView(false, false, "", null, null, PanelDisplays.Blank, RowActivity.None, RowActivity.None);
    }

    public PanelView View { get; private set; }

    /// <summary>Raised from a background thread whenever <see cref="View"/> changes.</summary>
    public event Action? ViewChanged;

    public void Start()
    {
        _panel.Input += OnInput;
        _panel.ConnectionChanged += OnPanelConnectionChanged;
        _sim.Changed += Render;
        _settings.Changed += OnSettingsChanged;

        _panel.Start();
        _sim.Start();
        _refresh = _time.CreateTimer(_ => Render(), null, RefreshInterval, RefreshInterval);
        Render();
    }

    private void OnPanelConnectionChanged(bool connected) => Render();

    private void OnSettingsChanged(BridgeSettings settings) => Render();

    private void OnInput(PanelEvent input)
    {
        try
        {
            lock (_gate)
            {
                switch (input)
                {
                    case SelectorMoved m:
                        _selectors[(int)m.Row] = m.Position;
                        _editBaro[(int)m.Row] = false;
                        _presses[(int)m.Row] = (0, 0);
                        _log.LogDebug("{Row} selector -> {Position}", m.Row, m.Position);
                        break;
                    case KnobTurned k:
                        var a = _activity[(int)k.Row];
                        _activity[(int)k.Row] = k.Knob == Knob.Inner
                            ? a with { InnerTurns = a.InnerTurns + k.Direction }
                            : a with { OuterTurns = a.OuterTurns + k.Direction };
                        OnKnob(k);
                        break;
                    case ButtonChanged { Pressed: true } b:
                        _activity[(int)b.Row] = _activity[(int)b.Row] with { ButtonDown = true, LongPress = false };
                        OnButtonDown(b.Row);
                        break;
                    case ButtonChanged b:
                        _activity[(int)b.Row] = _activity[(int)b.Row] with { ButtonDown = false, LongPress = false };
                        OnButtonUp(b.Row);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Handling {Input} failed.", input);
        }
        Render();
    }

    private void OnKnob(KnobTurned turn)
    {
        if (!_sim.IsConnected || _selectors[(int)turn.Row] is not Selector selector)
            return;

        var settings = _settings.Current;
        var state = Effective(_sim.State, _time.GetTimestamp());
        int steps = turn.Direction * _accelerator.Multiplier(turn, settings.Acceleration);
        bool outer = turn.Knob == Knob.Outer;

        switch (selector)
        {
            case Selector.Com1 or Selector.Com2 or Selector.Nav1 or Selector.Nav2:
                var radio = ToTunable(selector);
                int khz = state.Get(radio).StandbyKhz;
                if (khz <= 0)
                    return; // nothing to tune from yet
                int next = (radio, outer) switch
                {
                    (Tunable.Com1 or Tunable.Com2, true) => Frequencies.StepComMhz(khz, steps),
                    (Tunable.Com1 or Tunable.Com2, false) => Frequencies.StepComChannel(khz, steps, settings.ComSpacing),
                    (_, true) => Frequencies.StepNavMhz(khz, steps),
                    _ => Frequencies.StepNavChannel(khz, steps),
                };
                SetPending(FieldOf(radio), next);
                _sim.Send(new SetStandby(radio, next));
                _log.LogDebug("{Radio} standby {From} -> {To} ({Steps})", radio, khz, next, steps);
                break;

            case Selector.Adf:
                int index = turn.Row == PanelRow.Upper ? 1 : 2;
                int adf = state.GetAdf(index);
                if (adf <= 0)
                    return;
                int nextAdf = Math.Clamp(adf + steps * (outer ? 10 : 1), AdfMinKhz, AdfMaxKhz);
                SetPending(index == 1 ? Field.Adf1 : Field.Adf2, nextAdf);
                _sim.Send(new SetAdf(index, nextAdf));
                break;

            case Selector.Xpdr when _editBaro[(int)turn.Row]:
                if (double.IsNaN(state.BaroHpa))
                    return;
                double hpa = settings.BaroUnit == BaroUnit.InchesOfMercury
                    ? (Math.Round(state.BaroHpa / DisplayFormat.HpaPerInHg, 2) + steps * (outer ? 0.10 : 0.01)) * DisplayFormat.HpaPerInHg
                    : Math.Round(state.BaroHpa) + steps * (outer ? 10 : 1);
                hpa = Math.Clamp(hpa, BaroMinHpa, BaroMaxHpa);
                SetPending(Field.Baro, hpa);
                _sim.Send(new SetBaro(hpa));
                break;

            case Selector.Xpdr:
                if (state.Squawk < 0)
                    return;
                int code = outer ? Squawk.StepHighPair(state.Squawk, steps) : Squawk.StepLowPair(state.Squawk, steps);
                SetPending(Field.Squawk, code);
                _sim.Send(new SetSquawk(code));
                break;
        }
    }

    private void OnButtonDown(PanelRow row)
    {
        int i = (int)row;
        long now = _time.GetTimestamp();
        var (count, last) = _presses[i];
        bool quick = count > 0 && _time.GetElapsedTime(last, now).TotalMilliseconds <= _settings.Current.MultiPressGapMs;
        _presses[i] = (quick ? count + 1 : 1, now);

        _longPressFired[i] = false;
        _longPressTimers[i]?.Dispose();
        _longPressTimers[i] = _time.CreateTimer(_ => OnLongPress(row), null,
            TimeSpan.FromMilliseconds(_settings.Current.LongPressMs), Timeout.InfiniteTimeSpan);
    }

    private void OnButtonUp(PanelRow row)
    {
        int i = (int)row;
        _longPressTimers[i]?.Dispose();
        _longPressTimers[i] = null;
        if (_longPressFired[i] || _selectors[i] is not Selector selector)
            return;

        switch (selector)
        {
            case Selector.Com1 or Selector.Com2 or Selector.Nav1 or Selector.Nav2:
                var radio = ToTunable(selector);
                _pending.Remove(FieldOf(radio)); // the sim's swapped values are the truth now
                _sim.Send(new SwapRadio(radio));
                _log.LogDebug("Swap {Radio}", radio);
                break;
            case Selector.Xpdr when _presses[i].Count >= 3:
                // Triple press: the first two presses already toggled the edit focus there and back,
                // so this one leaves it alone and sets standard pressure instead.
                _presses[i] = (0, 0);
                _pending.Remove(Field.Baro);
                _sim.Send(new SetBaroStandard());
                _log.LogDebug("Baro STD");
                break;
            case Selector.Xpdr:
                _editBaro[i] = !_editBaro[i];
                break;
        }
    }

    private void OnLongPress(PanelRow row)
    {
        lock (_gate)
        {
            int i = (int)row;
            if (_longPressTimers[i] is null)
                return; // released just before the timer fired
            _longPressFired[i] = true;
            _presses[i] = (0, 0); // a hold never counts towards a triple press
            _activity[i] = _activity[i] with { LongPress = true };

            switch (_selectors[i])
            {
                case Selector.Xpdr:
                    var unit = _settings.Current.BaroUnit == BaroUnit.Hectopascal ? BaroUnit.InchesOfMercury : BaroUnit.Hectopascal;
                    _settings.Update(s => s with { BaroUnit = unit });
                    _log.LogInformation("Altimeter unit set to {Unit}.", unit);
                    break;
                case Selector.Com1 or Selector.Com2:
                    var spacing = _settings.Current.ComSpacing == ComSpacing.Khz833 ? ComSpacing.Khz25 : ComSpacing.Khz833;
                    _settings.Update(s => s with { ComSpacing = spacing });
                    _flash[i] = (spacing == ComSpacing.Khz833 ? "8.33" : "25", "", _time.GetTimestamp() + Ticks(FlashTime));
                    _log.LogInformation("COM spacing set to {Spacing}.", spacing);
                    break;
            }
        }
        Render();
    }

    private void Render()
    {
        PanelView view;
        lock (_gate)
        {
            long now = _time.GetTimestamp();
            var settings = _settings.Current;
            bool simConnected = _sim.IsConnected;
            var state = Effective(_sim.State, now);

            var (ul, ur) = RowText(PanelRow.Upper, state, simConnected, settings, now);
            var (ll, lr) = RowText(PanelRow.Lower, state, simConnected, settings, now);
            var displays = new PanelDisplays(ul, ur, ll, lr);
            _panel.Show(displays);

            view = new PanelView(_panel.IsConnected, simConnected, simConnected ? _sim.Name : "", _selectors[0], _selectors[1], displays, _activity[0], _activity[1]);
            if (view == View)
                return;
            View = view;
        }
        ViewChanged?.Invoke();
    }

    private (string Left, string Right) RowText(PanelRow row, RadioState s, bool simConnected, BridgeSettings settings, long now)
    {
        int i = (int)row;
        if (_flash[i] is { } flash)
        {
            if (now < flash.Until)
                return (flash.Left, flash.Right);
            _flash[i] = null;
        }

        if (!simConnected)
            return ("", "");

        bool compact = settings.CompactComDisplay;
        return _selectors[i] switch
        {
            Selector.Com1 => (DisplayFormat.Com(s.Com1.ActiveKhz, compact), DisplayFormat.Com(s.Com1.StandbyKhz, compact)),
            Selector.Com2 => (DisplayFormat.Com(s.Com2.ActiveKhz, compact), DisplayFormat.Com(s.Com2.StandbyKhz, compact)),
            Selector.Nav1 => (DisplayFormat.Nav(s.Nav1.ActiveKhz), DisplayFormat.Nav(s.Nav1.StandbyKhz)),
            Selector.Nav2 => (DisplayFormat.Nav(s.Nav2.ActiveKhz), DisplayFormat.Nav(s.Nav2.StandbyKhz)),
            Selector.Adf => (DisplayFormat.Adf(s.GetAdf(row == PanelRow.Upper ? 1 : 2)), ""),
            Selector.Dme => (DisplayFormat.Dme(s.Dme1Nm), DisplayFormat.Dme(s.Dme2Nm)),
            // Squawk on the left, altimeter on the right; the lit last point marks what the knobs edit.
            Selector.Xpdr => _editBaro[i]
                ? (DisplayFormat.Squawk(s.Squawk), DisplayFormat.WithFocus(DisplayFormat.Baro(s.BaroHpa, settings.BaroUnit)))
                : (DisplayFormat.WithFocus(DisplayFormat.Squawk(s.Squawk)), DisplayFormat.Baro(s.BaroHpa, settings.BaroUnit)),
            _ => ("", ""),
        };
    }

    /// <summary>The sim state with still-unconfirmed panel changes laid over it.</summary>
    private RadioState Effective(RadioState state, long now)
    {
        foreach (var (field, (value, until)) in _pending.ToList())
        {
            if (now >= until || Confirms(state, field, value))
            {
                _pending.Remove(field);
                continue;
            }
            state = Apply(state, field, value);
        }
        return state;
    }

    private void SetPending(Field field, double value) =>
        _pending[field] = (value, _time.GetTimestamp() + Ticks(HoldOff));

    private static bool Confirms(RadioState s, Field field, double value) => field switch
    {
        Field.Baro => Math.Abs(s.BaroHpa - value) < 0.1,
        _ => Read(s, field) == (int)value,
    };

    private static int Read(RadioState s, Field field) => field switch
    {
        Field.Com1 => s.Com1.StandbyKhz,
        Field.Com2 => s.Com2.StandbyKhz,
        Field.Nav1 => s.Nav1.StandbyKhz,
        Field.Nav2 => s.Nav2.StandbyKhz,
        Field.Adf1 => s.Adf1Khz,
        Field.Adf2 => s.Adf2Khz,
        _ => s.Squawk,
    };

    private static RadioState Apply(RadioState s, Field field, double value) => field switch
    {
        Field.Com1 or Field.Com2 or Field.Nav1 or Field.Nav2 =>
            s.With(RadioOf(field), s.Get(RadioOf(field)) with { StandbyKhz = (int)value }),
        Field.Adf1 => s with { Adf1Khz = (int)value },
        Field.Adf2 => s with { Adf2Khz = (int)value },
        Field.Squawk => s with { Squawk = (int)value },
        _ => s with { BaroHpa = value },
    };

    private static Tunable ToTunable(Selector selector) => selector switch
    {
        Selector.Com1 => Tunable.Com1,
        Selector.Com2 => Tunable.Com2,
        Selector.Nav1 => Tunable.Nav1,
        _ => Tunable.Nav2,
    };

    private static Field FieldOf(Tunable radio) => (Field)(int)radio;

    private static Tunable RadioOf(Field field) => (Tunable)(int)field;

    private long Ticks(TimeSpan span) => (long)(span.TotalSeconds * _time.TimestampFrequency);

    public void Dispose()
    {
        _refresh?.Dispose();
        _panel.Input -= OnInput;
        _panel.ConnectionChanged -= OnPanelConnectionChanged;
        _sim.Changed -= Render;
        _settings.Changed -= OnSettingsChanged;
        lock (_gate)
        {
            foreach (var timer in _longPressTimers)
                timer?.Dispose();
        }
    }
}
