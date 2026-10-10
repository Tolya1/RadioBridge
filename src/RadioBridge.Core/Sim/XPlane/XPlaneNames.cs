using RadioBridge.Core.Radio;

namespace RadioBridge.Core.Sim.XPlane;

/// <summary>X-Plane 12 dataref and command names, and conversions between their units and <see cref="RadioState"/>.</summary>
internal static class XPlaneNames
{
    private const string Actuators = "sim/cockpit2/radios/actuators/";
    private const string Indicators = "sim/cockpit2/radios/indicators/";

    public const string Squawk = Actuators + "transponder_code";
    public const string Baro = "sim/cockpit2/gauges/actuators/barometer_setting_in_hg_pilot";

    /// <summary>
    /// Standard pressure as an altimeter setting in inHg. X-Plane's own command sets its internal 1013.25 hPa
    /// (29.92125 inHg), which no inHg altimeter can be set to, so standard pressure is written as 29.92
    /// (shown as 1013 by an hPa altimeter) instead.
    /// </summary>
    public const double StandardBaroInHg = 29.92;

    /// <summary>
    /// Added to every altimeter setting written. The dataref is a 32-bit float, so a value like 29.91 is
    /// stored as 29.909999…, and cockpits that cut digits off rather than round show it one step too low:
    /// the Thranda PC-6 draws floor(inHg × 100) in inHg and floor(inHg × 33.864) in hPa. A margin far
    /// below anything an altimeter shows (0.002 hPa) puts the stored value just above the setting instead.
    /// </summary>
    public const double BaroMarginInHg = 0.00005;

    // COM uses the *_hz_833 datarefs (whole kHz) so 8.33 channels survive the round trip; the plain
    // COM datarefs have 10 kHz resolution. NAV datarefs are in units of 10 kHz.
    public static string Active(Tunable radio) => Actuators + radio switch
    {
        Tunable.Com1 => "com1_frequency_hz_833",
        Tunable.Com2 => "com2_frequency_hz_833",
        Tunable.Nav1 => "nav1_frequency_hz",
        _ => "nav2_frequency_hz",
    };

    public static string Standby(Tunable radio) => Actuators + radio switch
    {
        Tunable.Com1 => "com1_standby_frequency_hz_833",
        Tunable.Com2 => "com2_standby_frequency_hz_833",
        Tunable.Nav1 => "nav1_standby_frequency_hz",
        _ => "nav2_standby_frequency_hz",
    };

    // "standy" is Laminar's spelling.
    public static string Flip(Tunable radio) => "sim/radios/" + radio switch
    {
        Tunable.Com1 => "com1_standy_flip",
        Tunable.Com2 => "com2_standy_flip",
        Tunable.Nav1 => "nav1_standy_flip",
        _ => "nav2_standy_flip",
    };

    public static string Adf(int index) => $"{Actuators}adf{index}_frequency_hz";

    public static string Dme(int index) => $"{Indicators}nav{index}_dme_distance_nm";

    private static readonly Dictionary<string, Func<RadioState, double, RadioState>> Readers = BuildReaders();

    public static IReadOnlyCollection<string> Datarefs => Readers.Keys;

    public static readonly string[] Commands =
    [
        Flip(Tunable.Com1), Flip(Tunable.Com2), Flip(Tunable.Nav1), Flip(Tunable.Nav2),
    ];

    /// <summary>Applies one raw dataref value to the state; unknown names leave it unchanged.</summary>
    public static RadioState Apply(RadioState state, string dataref, double value) =>
        Readers.TryGetValue(dataref, out var read) ? read(state, value) : state;

    /// <summary>Raw value for a frequency dataref.</summary>
    public static long FrequencyRaw(Tunable radio, int khz) => IsCom(radio) ? khz : khz / 10;

    /// <summary>
    /// The altimeter setting to write, in inHg (X-Plane's unit): exactly the setting the knob chose, plus
    /// <see cref="BaroMarginInHg"/>. In hPa that is the whole hPa (1040 hPa = 30.7112 inHg, which is also
    /// what X-Plane's own knob stores in hPa), in inHg the two-decimal value (29.91). Rounding an hPa setting
    /// to two inHg decimals instead would move it: 1013 hPa would become 29.91 inHg = 1012.9 hPa, and an
    /// altimeter that truncates shows 1012.
    /// </summary>
    public static double BaroRaw(double hpa) => hpa / DisplayFormat.HpaPerInHg + BaroMarginInHg;

    /// <summary>Standard pressure as written: 29.92 inHg, plus the margin.</summary>
    public static double StandardBaroRaw => StandardBaroInHg + BaroMarginInHg;

    private static bool IsCom(Tunable radio) => radio is Tunable.Com1 or Tunable.Com2;

    private static int ToKhz(Tunable radio, double raw) =>
        IsCom(radio) ? Frequencies.ComChannelKhz(raw / 1000) : (int)Math.Round(raw) * 10;

    private static Dictionary<string, Func<RadioState, double, RadioState>> BuildReaders()
    {
        var readers = new Dictionary<string, Func<RadioState, double, RadioState>>();
        foreach (var radio in Enum.GetValues<Tunable>())
        {
            readers[Active(radio)] = (s, v) => s.With(radio, s.Get(radio) with { ActiveKhz = ToKhz(radio, v) });
            readers[Standby(radio)] = (s, v) => s.With(radio, s.Get(radio) with { StandbyKhz = ToKhz(radio, v) });
        }
        readers[Adf(1)] = (s, v) => s with { Adf1Khz = (int)Math.Round(v) };
        readers[Adf(2)] = (s, v) => s with { Adf2Khz = (int)Math.Round(v) };
        readers[Dme(1)] = (s, v) => s with { Dme1Nm = v };
        readers[Dme(2)] = (s, v) => s with { Dme2Nm = v };
        readers[Squawk] = (s, v) => s with { Squawk = (int)Math.Round(v) };
        readers[Baro] = (s, v) => s with { BaroHpa = v * DisplayFormat.HpaPerInHg };
        return readers;
    }
}
