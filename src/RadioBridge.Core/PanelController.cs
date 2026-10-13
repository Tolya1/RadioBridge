using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RadioBridge.Core.Diagnostics;
using RadioBridge.Core.Panel;
using RadioBridge.Core.Radio;
using RadioBridge.Core.Settings;
using RadioBridge.Core.Sim;

namespace RadioBridge.Core;

/// <summary>What the UI shows: connection state, selector positions, the four displays and live input.</summary>
/// <param name="Notice">What keeps a simulator from connecting, when the user can do something about it.</param>
/// <param name="UpperGps">Set while the upper row drives a GPS (GPS control on, selector on ADF or DME).</param>
public sealed record PanelView(
    bool PanelConnected,
    bool SimConnected,
    string SimName,
    Selector? Upper,
    Selector? Lower,
    PanelDisplays Displays,
    RowActivity UpperActivity,
    RowActivity LowerActivity,
    SimNotice? Notice = null,
    GpsRow? UpperGps = null,
    GpsRow? LowerGps = null);

/// <summary>A row driving a GPS: which one (1 or 2), and on DME the key a tap on ACT/STBY presses (null on ADF).</summary>
public sealed record GpsRow(int Unit, string? Key);

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
/// Values the knobs change, and the frequencies an ACT/STBY press swaps, are shown immediately and kept
/// for a short hold-off, so slightly older values still on their way back from the sim cannot make the
/// display jump back or eat detents during a fast spin. Once the sim reports the same value, or the
/// hold-off ends, the sim is authoritative again (so a radio that snaps a frequency, or doesn't swap, is
/// shown truthfully).
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

    /// <summary>
    /// The keys the inner knob chooses from on a DME row driving a GPS, counted in clicks from D→ (0): CLR held one
    /// click to the left, then MENU, FPL, PROC, CDI, OBS, MSG and VNAV to the right. The most used come first.
    /// </summary>
    private static readonly (GpsKey Key, string Name)[] GpsChoices =
    [
        (GpsKey.ClearHeld, "CLR held"),
        (GpsKey.DirectTo, "D→"),
        (GpsKey.Menu, "MENU"),
        (GpsKey.FlightPlan, "FPL"),
        (GpsKey.Procedure, "PROC"),
        (GpsKey.Cdi, "CDI"),
        (GpsKey.Obs, "OBS"),
        (GpsKey.Message, "MSG"),
        (GpsKey.Vnav, "VNAV"),
    ];

    private const int FirstGpsChoice = -1;
    private static readonly int LastGpsChoice = FirstGpsChoice + GpsChoices.Length - 1;

    private readonly IRadioPanel _panel;
    private readonly ISimBackend _sim;
    private readonly SettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly Recorder _recorder;
    private readonly KnobAccelerator _accelerator;
    private readonly object _gate = new();

    private readonly Selector?[] _selectors = new Selector?[2];
    private readonly bool[] _editBaro = new bool[2];
    private readonly int[] _gpsChoice = new int[2]; // clicks from D→, see GpsChoices
    private readonly ITimer?[] _longPressTimers = new ITimer?[2];
    private readonly bool[] _longPressFired = new bool[2];
    private readonly (string Left, string Right, long Until)?[] _flash = new (string, string, long)?[2];
    private readonly Dictionary<Field, (double Value, long Until)> _pending = new();
    private readonly Dictionary<Tunable, (RadioPair Expected, long Until)> _swapped = new();
    private readonly RowActivity[] _activity = [RowActivity.None, RowActivity.None];
    private PanelDisplays? _recordedDisplays;
    private ITimer? _refresh;

    public PanelController(IRadioPanel panel, ISimBackend sim, SettingsStore settings, TimeProvider? time = null,
        ILogger<PanelController>? log = null, Recorder? recorder = null)
    {
        _panel = panel;
        _sim = sim;
        _settings = settings;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger<PanelController>.Instance;
        _recorder = recorder ?? new Recorder();
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
        RecordInput(input);
        try
        {
            lock (_gate)
            {
                switch (input)
                {
                    case SelectorMoved m:
                        _selectors[(int)m.Row] = m.Position;
                        _editBaro[(int)m.Row] = false;
                        _gpsChoice[(int)m.Row] = 0;
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

    private void RecordInput(PanelEvent input) => _recorder.Record("panel", "input", w =>
    {
        w.WriteString("row", input.Row.ToString());
        switch (input)
        {
            case SelectorMoved m:
                w.WriteString("selector", m.Position.ToString());
                break;
            case KnobTurned k:
                w.WriteString("knob", k.Knob.ToString());
                w.WriteNumber("direction", k.Direction);
                break;
            case ButtonChanged b:
                w.WriteString("button", b.Pressed ? "down" : "up");
                break;
        }
    });

    /// <summary>Records something the controller decided, with the row's selector for context.</summary>
    private void RecordAction(string action, PanelRow row, Action<System.Text.Json.Utf8JsonWriter>? fields = null) =>
        _recorder.Record("controller", action, w =>
        {
            w.WriteString("row", row.ToString());
            w.WriteString("selector", _selectors[(int)row]?.ToString());
            fields?.Invoke(w);
        });

    private void OnKnob(KnobTurned turn)
    {
        if (!_sim.IsConnected || _selectors[(int)turn.Row] is not Selector selector)
        {
            RecordAction("ignored", turn.Row, w => w.WriteString("why", _sim.IsConnected ? "no selector position yet" : "no simulator"));
            return;
        }

        var settings = _settings.Current;
        var state = Effective(_sim.State, _time.GetTimestamp());
        // Only COM frequencies span enough steps to need acceleration; everything else moves one step
        // per detent. The accelerator still sees every detent, so its timing stays current.
        int multiplier = _accelerator.Multiplier(turn, settings.ComAcceleration);
        int steps = turn.Direction * (selector is Selector.Com1 or Selector.Com2 ? multiplier : 1);
        bool outer = turn.Knob == Knob.Outer;

        if (GpsUnit(turn.Row, selector) is int unit)
        {
            OnGpsKnob(turn, selector, unit);
            return;
        }

        switch (selector)
        {
            case Selector.Com1 or Selector.Com2 or Selector.Nav1 or Selector.Nav2:
                var radio = ToTunable(selector);
                int khz = state.Get(radio).StandbyKhz;
                if (khz <= 0)
                {
                    RecordAction("ignored", turn.Row, w => w.WriteString("why", "the simulator reports no standby frequency"));
                    return; // nothing to tune from yet
                }
                int next = (radio, outer) switch
                {
                    (Tunable.Com1 or Tunable.Com2, true) => Frequencies.StepComMhz(khz, steps),
                    (Tunable.Com1 or Tunable.Com2, false) => Frequencies.StepComChannel(khz, steps, settings.ComSpacing),
                    (_, true) => Frequencies.StepNavMhz(khz, steps),
                    _ => Frequencies.StepNavChannel(khz, steps),
                };
                SetPending(FieldOf(radio), next);
                SendValue(new SetStandby(radio, next), (KnobTarget)(int)radio, outer, steps, khz, next);
                _log.LogDebug("{Radio} standby {From} -> {To} ({Steps})", radio, khz, next, steps);
                RecordTune(turn.Row, khz, next, steps, multiplier);
                break;

            case Selector.Adf:
                int index = turn.Row == PanelRow.Upper ? 1 : 2;
                int adf = state.GetAdf(index);
                if (adf <= 0)
                    return;
                int nextAdf = Math.Clamp(adf + steps * (outer ? 10 : 1), AdfMinKhz, AdfMaxKhz);
                SetPending(index == 1 ? Field.Adf1 : Field.Adf2, nextAdf);
                SendValue(new SetAdf(index, nextAdf), index == 1 ? KnobTarget.Adf1 : KnobTarget.Adf2, outer, steps, adf, nextAdf);
                RecordTune(turn.Row, adf, nextAdf, steps, 1);
                break;

            case Selector.Xpdr when _editBaro[(int)turn.Row]:
                if (double.IsNaN(state.BaroHpa))
                    return;
                double hpa = settings.BaroUnit == BaroUnit.InchesOfMercury
                    ? (Math.Round(state.BaroHpa / DisplayFormat.HpaPerInHg, 2) + steps * (outer ? 0.10 : 0.01)) * DisplayFormat.HpaPerInHg
                    : Math.Round(state.BaroHpa) + steps * (outer ? 10 : 1);
                hpa = Math.Clamp(hpa, BaroMinHpa, BaroMaxHpa);
                SetPending(Field.Baro, hpa);
                SendValue(new SetBaro(hpa), KnobTarget.Baro, outer, steps, state.BaroHpa, hpa);
                RecordTune(turn.Row, state.BaroHpa, hpa, steps, 1, settings.BaroUnit.ToString());
                break;

            case Selector.Xpdr:
                if (state.Squawk < 0)
                    return;
                int code = outer ? Squawk.StepHighPair(state.Squawk, steps) : Squawk.StepLowPair(state.Squawk, steps);
                SetPending(Field.Squawk, code);
                SendValue(new SetSquawk(code), KnobTarget.Squawk, outer, steps, state.Squawk, code);
                RecordTune(turn.Row, state.Squawk, code, steps, 1);
                break;
        }
    }

    /// <summary>
    /// Sends a value the knobs changed: <paramref name="set"/>, or for an aircraft whose radios can only be turned
    /// (<see cref="ISimBackend.TurnsKnobs"/>) the knob turn that leads from <paramref name="from"/> to <paramref name="to"/>.
    /// </summary>
    private void SendValue(SimCommand set, KnobTarget target, bool outer, int steps, double from, double to) =>
        _sim.Send(_sim.TurnsKnobs ? new TurnKnobs(target, outer, steps, from, to) : set);

    private void RecordTune(PanelRow row, double from, double to, int steps, int multiplier, string? unit = null) =>
        RecordAction("tune", row, w =>
        {
            w.WriteNumberOrNull("from", from);
            w.WriteNumber("to", to);
            w.WriteNumber("steps", steps);
            if (multiplier != 1)
                w.WriteNumber("acceleration", multiplier);
            if (unit is not null)
                w.WriteString("unit", unit);
        });

    /// <summary>
    /// The GPS a row drives: GPS 1 for the upper row, <see cref="BridgeSettings.LowerRowGps"/> for the lower one.
    /// Null unless GPS control is on, the selector is on ADF or DME and the simulator takes GPS keys.
    /// </summary>
    private int? GpsUnit(PanelRow row, Selector? selector)
    {
        var settings = _settings.Current;
        if (selector is not (Selector.Adf or Selector.Dme) || !settings.GpsControl || !_sim.SupportsGpsKeys)
            return null;
        return row == PanelRow.Upper || settings.LowerRowGps == 1 ? 1 : 2;
    }

    private static (GpsKey Key, string Name) GpsChoice(int clicks) => GpsChoices[clicks - FirstGpsChoice];

    /// <summary>ADF: the GNS's right-hand knobs. DME: the outer knob sets the map range, the inner one chooses the key a tap presses.</summary>
    private void OnGpsKnob(KnobTurned turn, Selector selector, int unit)
    {
        bool up = turn.Direction > 0;
        if (selector == Selector.Dme && turn.Knob == Knob.Inner)
        {
            int i = (int)turn.Row;
            int choice = Math.Clamp(_gpsChoice[i] + turn.Direction, FirstGpsChoice, LastGpsChoice);
            if (choice == _gpsChoice[i])
                return;
            _gpsChoice[i] = choice;
            RecordAction("choose", turn.Row, w =>
            {
                w.WriteNumber("unit", unit);
                w.WriteString("key", GpsChoice(choice).Key.ToString());
            });
            return;
        }

        var key = (selector, turn.Knob) switch
        {
            (Selector.Dme, _) => up ? GpsKey.ZoomOut : GpsKey.ZoomIn, // clockwise shows more of the map
            (_, Knob.Outer) => up ? GpsKey.LargeKnobUp : GpsKey.LargeKnobDown,
            _ => up ? GpsKey.SmallKnobUp : GpsKey.SmallKnobDown,
        };
        PressGps(turn.Row, unit, key);
    }

    private void PressGps(PanelRow row, int unit, GpsKey key)
    {
        _sim.Send(new PressGpsKey(unit, key));
        _log.LogDebug("GPS {Unit} {Key}", unit, key);
        RecordAction("gps", row, w =>
        {
            w.WriteNumber("unit", unit);
            w.WriteString("key", key.ToString());
        });
    }

    private void OnButtonDown(PanelRow row)
    {
        int i = (int)row;
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

        if (GpsUnit(row, selector) is int unit)
        {
            // ADF: ENT. DME: the chosen key, and the choice goes back to D→ so a plain tap is always Direct-to.
            PressGps(row, unit, selector == Selector.Adf ? GpsKey.Enter : GpsChoice(_gpsChoice[i]).Key);
            _gpsChoice[i] = 0;
            return;
        }

        switch (selector)
        {
            case Selector.Com1 or Selector.Com2 or Selector.Nav1 or Selector.Nav2:
                var radio = ToTunable(selector);
                long now = _time.GetTimestamp();
                // Show the swap right away (including a standby value the knobs just set and the sim hasn't
                // confirmed yet). Some radios take a moment: they swap when the press ends, in two steps.
                var shown = Effective(_sim.State, now).Get(radio);
                _pending.Remove(FieldOf(radio));
                var swapped = new RadioPair(shown.StandbyKhz, shown.ActiveKhz);
                if (_sim.IsConnected && shown.ActiveKhz > 0 && shown.StandbyKhz > 0)
                    _swapped[radio] = (swapped, now + Ticks(HoldOff));
                _sim.Send(new SwapRadio(radio));
                _log.LogDebug("Swap {Radio}", radio);
                RecordAction("swap", row, w =>
                {
                    w.WriteString("radio", radio.ToString());
                    w.WriteNumber("active", swapped.ActiveKhz);
                    w.WriteNumber("standby", swapped.StandbyKhz);
                });
                break;
            case Selector.Xpdr:
                _editBaro[i] = !_editBaro[i];
                RecordAction("focus", row, w => w.WriteString("edits", _editBaro[i] ? "altimeter" : "squawk"));
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
            _activity[i] = _activity[i] with { LongPress = true };

            if (GpsUnit(row, _selectors[i]) is int unit)
                PressGps(row, unit, _selectors[i] == Selector.Adf ? GpsKey.Cursor : GpsKey.Clear); // ADF: CRSR, the small-knob push
            else
                OnRadioLongPress(row);
        }
        Render();
    }

    /// <summary>A long press on a row that drives the radios (under the gate).</summary>
    private void OnRadioLongPress(PanelRow row)
    {
        int i = (int)row;
        switch (_selectors[i])
        {
            // The hold acts on whichever XPDR value the knobs edit.
            case Selector.Xpdr when _editBaro[i]:
                _pending.Remove(Field.Baro);
                _sim.Send(new SetBaroStandard());
                _log.LogDebug("Baro STD");
                RecordAction("hold", row, w => w.WriteString("action", "standard pressure"));
                break;
            case Selector.Xpdr when _sim.IsConnected:
                int vfr = _settings.Current.VfrSquawk;
                SetPending(Field.Squawk, vfr);
                SendValue(new SetSquawk(vfr), KnobTarget.Squawk, false, 0, Effective(_sim.State, _time.GetTimestamp()).Squawk, vfr);
                _log.LogDebug("Squawk VFR {Code:0000}", vfr);
                RecordAction("hold", row, w => w.WriteString("action", $"VFR squawk {vfr:0000}"));
                break;
            case Selector.Com1 or Selector.Com2:
                var spacing = _settings.Current.ComSpacing == ComSpacing.Khz833 ? ComSpacing.Khz25 : ComSpacing.Khz833;
                _settings.Update(s => s with { ComSpacing = spacing });
                _flash[i] = (spacing == ComSpacing.Khz833 ? "8.33" : "25", "", _time.GetTimestamp() + Ticks(FlashTime));
                _log.LogInformation("COM spacing set to {Spacing}.", spacing);
                RecordAction("hold", row, w => w.WriteString("action", $"COM spacing {spacing}"));
                break;
            default:
                RecordAction("hold", row, w => w.WriteString("action", "none"));
                break;
        }
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

            if (displays != _recordedDisplays && _recorder.IsRecording)
            {
                _recordedDisplays = displays;
                _recorder.Record("controller", "displays", w =>
                {
                    w.WriteString("upperLeft", displays.UpperLeft);
                    w.WriteString("upperRight", displays.UpperRight);
                    w.WriteString("lowerLeft", displays.LowerLeft);
                    w.WriteString("lowerRight", displays.LowerRight);
                });
            }

            view = new PanelView(_panel.IsConnected, simConnected, simConnected ? _sim.Name : "", _selectors[0], _selectors[1], displays,
                _activity[0], _activity[1], _sim.Notice, GpsRowOf(PanelRow.Upper), GpsRowOf(PanelRow.Lower));
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

        if (GpsUnit(row, _selectors[i]) is int unit)
        {
            // ADF: what you fly by. DME: the chosen key's number (0 = D→) and the distance.
            var gps = s.GetGps(unit);
            return _selectors[i] == Selector.Adf
                ? (DisplayFormat.GpsDistance(gps), DisplayFormat.GpsTrack(gps))
                : (_gpsChoice[i].ToString(System.Globalization.CultureInfo.InvariantCulture), DisplayFormat.GpsDistance(gps));
        }

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

    private GpsRow? GpsRowOf(PanelRow row)
    {
        int i = (int)row;
        return GpsUnit(row, _selectors[i]) is int unit
            ? new GpsRow(unit, _selectors[i] == Selector.Dme ? GpsChoice(_gpsChoice[i]).Name : null)
            : null;
    }

    /// <summary>The sim state with still-unconfirmed panel changes laid over it.</summary>
    private RadioState Effective(RadioState state, long now)
    {
        var sim = state;
        foreach (var (radio, (expected, until)) in _swapped.ToList())
        {
            var actual = sim.Get(radio);
            // A standby value the knobs set after the swap is the pending value's business, not the swap's.
            bool done = actual.ActiveKhz == expected.ActiveKhz
                        && (actual.StandbyKhz == expected.StandbyKhz || _pending.ContainsKey(FieldOf(radio)));
            if (done || now >= until)
            {
                _swapped.Remove(radio);
                if (!done)
                    RecordUnconfirmed($"{radio} swap", $"{expected.ActiveKhz}/{expected.StandbyKhz}", $"{actual.ActiveKhz}/{actual.StandbyKhz}");
                continue;
            }
            state = state.With(radio, expected);
        }

        foreach (var (field, (value, until)) in _pending.ToList())
        {
            bool confirmed = Confirms(sim, field, value);
            if (confirmed || now >= until)
            {
                _pending.Remove(field);
                if (!confirmed)
                    RecordUnconfirmed(field.ToString(), value.ToString(System.Globalization.CultureInfo.InvariantCulture), SimValue(sim, field));
                continue;
            }
            state = Apply(state, field, value);
        }
        return state;
    }

    /// <summary>The sim didn't take a value within the hold-off (or changed it right back): worth knowing in a recording.</summary>
    private void RecordUnconfirmed(string what, string sent, string simulator) =>
        _recorder.Record("controller", "unconfirmed", w =>
        {
            w.WriteString("what", what);
            w.WriteString("sent", sent);
            w.WriteString("simulator", simulator);
        });

    private static string SimValue(RadioState s, Field field) => field == Field.Baro
        ? s.BaroHpa.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
        : Read(s, field).ToString(System.Globalization.CultureInfo.InvariantCulture);

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
