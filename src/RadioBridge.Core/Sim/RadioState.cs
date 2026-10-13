namespace RadioBridge.Core.Sim;

/// <summary>A radio with an active and a standby frequency.</summary>
public enum Tunable
{
    Com1,
    Com2,
    Nav1,
    Nav2,
}

public readonly record struct RadioPair(int ActiveKhz, int StandbyKhz);

/// <summary>Where a GPS is taking the aircraft: distance (nm) and desired track (degrees magnetic) to the active waypoint.</summary>
/// <param name="HasWaypoint">The GPS has an active waypoint (its CDI isn't flagged); otherwise the numbers mean nothing.</param>
public readonly record struct GpsNav(double DistanceNm, double TrackDeg, bool HasWaypoint)
{
    public static readonly GpsNav None = new(double.NaN, double.NaN, false);
}

/// <summary>A key press or knob click on a GNS 430/530, named after the unit's own controls.</summary>
public enum GpsKey
{
    /// <summary>Large right knob clockwise: next chapter, or with the cursor on, the next field or character.</summary>
    LargeKnobUp,
    LargeKnobDown,

    /// <summary>Small right knob clockwise: next page, or with the cursor on, the next character.</summary>
    SmallKnobUp,
    SmallKnobDown,

    /// <summary>CRSR: pushing the small right knob.</summary>
    Cursor,
    Enter,
    Clear,

    /// <summary>CLR held: back to the default NAV page.</summary>
    ClearHeld,
    DirectTo,
    Menu,
    FlightPlan,
    Procedure,
    Cdi,
    Obs,
    Message,
    Vnav,

    /// <summary>Map range smaller.</summary>
    ZoomIn,

    /// <summary>Map range larger.</summary>
    ZoomOut,
}

/// <summary>
/// Radio values as the simulator reports them, in sim-neutral units: COM/NAV in kHz (COM as 8.33
/// channel names), ADF in kHz, DME in nautical miles (NaN = none), squawk as four octal digits and
/// the pilot's altimeter setting in hPa. Immutable, so it can be handed across threads.
/// </summary>
public sealed record RadioState
{
    public static readonly RadioState Empty = new();

    public RadioPair Com1 { get; init; }
    public RadioPair Com2 { get; init; }
    public RadioPair Nav1 { get; init; }
    public RadioPair Nav2 { get; init; }
    public int Adf1Khz { get; init; }
    public int Adf2Khz { get; init; }
    public double Dme1Nm { get; init; } = double.NaN;
    public double Dme2Nm { get; init; } = double.NaN;
    public int Squawk { get; init; } = -1;
    public double BaroHpa { get; init; } = double.NaN;
    public GpsNav Gps1 { get; init; } = GpsNav.None;
    public GpsNav Gps2 { get; init; } = GpsNav.None;

    public GpsNav GetGps(int unit) => unit == 2 ? Gps2 : Gps1;

    public RadioState WithGps(int unit, GpsNav gps) => unit == 2 ? this with { Gps2 = gps } : this with { Gps1 = gps };

    public RadioPair Get(Tunable radio) => radio switch
    {
        Tunable.Com1 => Com1,
        Tunable.Com2 => Com2,
        Tunable.Nav1 => Nav1,
        _ => Nav2,
    };

    public RadioState With(Tunable radio, RadioPair pair) => radio switch
    {
        Tunable.Com1 => this with { Com1 = pair },
        Tunable.Com2 => this with { Com2 = pair },
        Tunable.Nav1 => this with { Nav1 = pair },
        _ => this with { Nav2 = pair },
    };

    public int GetAdf(int index) => index == 2 ? Adf2Khz : Adf1Khz;

    public RadioState WithAdf(int index, int khz) => index == 2 ? this with { Adf2Khz = khz } : this with { Adf1Khz = khz };
}

/// <summary>Something the panel asks the simulator to do.</summary>
public abstract record SimCommand;

public sealed record SetStandby(Tunable Radio, int Khz) : SimCommand;

public sealed record SwapRadio(Tunable Radio) : SimCommand;

/// <param name="Index">1 or 2.</param>
public sealed record SetAdf(int Index, int Khz) : SimCommand;

public sealed record SetSquawk(int Code) : SimCommand;

public sealed record SetBaro(double Hpa) : SimCommand;

public sealed record SetBaroStandard : SimCommand;

/// <param name="Unit">The GPS: 1 or 2.</param>
public sealed record PressGpsKey(int Unit, GpsKey Key) : SimCommand;

/// <summary>A value the panel's knobs change.</summary>
public enum KnobTarget
{
    Com1,
    Com2,
    Nav1,
    Nav2,
    Adf1,
    Adf2,
    Squawk,
    Baro,
}

/// <summary>
/// A knob turn, for aircraft whose radios can only be turned, not set (<see cref="ISimBackend.TurnsKnobs"/>), sent
/// instead of the matching Set command: which knob and how many detents (acceleration included, negative =
/// anticlockwise), and the value it goes from and should lead to, in <see cref="RadioState"/> units. A hold that
/// sets a value (VFR squawk) comes as a turn of 0 steps from the current value to that one.
/// </summary>
public sealed record TurnKnobs(KnobTarget Target, bool Outer, int Steps, double From, double To) : SimCommand;
