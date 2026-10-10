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
