using RadioBridge.Core.Radio;

namespace RadioBridge.Core.Sim;

/// <summary>
/// Built-in radios for trying the panel without a simulator ("test mode"). It is connected while
/// test mode is on, and <see cref="SimRouter"/> only uses it when no real simulator is connected.
/// Commands apply instantly; DME1 counts down and DME2 up, so live updates are visible on the panel.
/// </summary>
public sealed class TestSimBackend : ISimBackend
{
    public static readonly RadioState InitialState = new()
    {
        Com1 = new RadioPair(124_355, 121_500),
        Com2 = new RadioPair(122_800, 118_005),
        Nav1 = new RadioPair(110_300, 113_500),
        Nav2 = new RadioPair(113_900, 108_000),
        Adf1Khz = 362,
        Adf2Khz = 1_160,
        Dme1Nm = 18.0,
        Dme2Nm = 6.4,
        Squawk = 7000,
        BaroHpa = 1013.25,
    };

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private readonly Func<bool> _enabled;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private ITimer? _timer;
    private RadioState _state = InitialState;
    private bool _connected;

    /// <param name="enabled">Whether test mode is on; re-read every second and on <see cref="Refresh"/>.</param>
    public TestSimBackend(Func<bool> enabled, TimeProvider? time = null)
    {
        _enabled = enabled;
        _time = time ?? TimeProvider.System;
    }

    public string Name => "Test mode";

    public bool IsConnected
    {
        get { lock (_gate) return _connected; }
    }

    public RadioState State
    {
        get { lock (_gate) return _connected ? _state : RadioState.Empty; }
    }

    public event Action? Changed;

    public void Start()
    {
        _timer ??= _time.CreateTimer(_ => OnTick(), null, Tick, Tick);
        Refresh();
    }

    /// <summary>Picks up a change of the test-mode switch right away.</summary>
    public void Refresh()
    {
        bool changed;
        lock (_gate)
        {
            bool enabled = _enabled();
            changed = enabled != _connected;
            _connected = enabled;
        }
        if (changed)
            Changed?.Invoke();
    }

    public void Send(SimCommand command)
    {
        lock (_gate)
        {
            if (!_connected)
                return;
            var s = _state;
            _state = command switch
            {
                SetStandby { Radio: Tunable.Com1 or Tunable.Com2 } c =>
                    s.With(c.Radio, s.Get(c.Radio) with { StandbyKhz = Frequencies.ComChannelKhz(c.Khz / 1000.0) }),
                SetStandby c => s.With(c.Radio, s.Get(c.Radio) with { StandbyKhz = Frequencies.NavKhz(c.Khz / 1000.0) }),
                SwapRadio c => s.With(c.Radio, new RadioPair(s.Get(c.Radio).StandbyKhz, s.Get(c.Radio).ActiveKhz)),
                SetAdf c => s.WithAdf(c.Index, c.Khz),
                SetSquawk c => s with { Squawk = c.Code },
                SetBaro c => s with { BaroHpa = c.Hpa },
                SetBaroStandard => s with { BaroHpa = 1013.25 },
                _ => s,
            };
        }
        Changed?.Invoke();
    }

    private void OnTick()
    {
        Refresh();
        lock (_gate)
        {
            if (!_connected)
                return;
            // Inbound to station 1, outbound from station 2.
            double dme1 = Math.Round(_state.Dme1Nm - 0.1, 1);
            double dme2 = Math.Round(_state.Dme2Nm + 0.1, 1);
            _state = _state with { Dme1Nm = dme1 < 0.5 ? 30.0 : dme1, Dme2Nm = dme2 > 30.0 ? 0.5 : dme2 };
        }
        Changed?.Invoke();
    }

    public void Dispose() => _timer?.Dispose();
}
