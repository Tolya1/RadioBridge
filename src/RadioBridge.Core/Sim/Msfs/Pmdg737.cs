namespace RadioBridge.Core.Sim.Msfs;

/// <summary>
/// The PMDG 737 (-600 to -900, MSFS 2020 and 2024) ignores MSFS's radio events: its radios, transponder and
/// altimeter react to PMDG's own control events, which act like a mouse on the cockpit control
/// (Documentation\SDK\PMDG_NG3_SDK.h and PMDG_737_MSFS_SDK.pdf in the aircraft's package). An event goes out as
/// the client event "#&lt;69632 + its number&gt;" with a mouse flag as its parameter: a wheel step turns a knob one
/// detent, a click and a release press a switch. Sending them needs nothing enabled in the aircraft (only reading
/// PMDG's own data does). The radios' values still come from MSFS's usual SimVars.
/// </summary>
internal static class Pmdg737
{
    /// <summary>THIRD_PARTY_EVENT_ID_MIN: PMDG's event numbers count from here.</summary>
    public const uint EventBase = 0x00011000;

    public const uint MouseLeftSingle = 0x20000000;
    public const uint MouseLeftRelease = 0x00020000;
    public const uint MouseWheelUp = 0x00004000;
    public const uint MouseWheelDown = 0x00002000;

    // PMDG_NG3_SDK.h: EVT_* = THIRD_PARTY_EVENT_ID_MIN + these.
    public const uint Com1Transfer = 721, Com1Outer = 726, Com1Inner = 727;
    public const uint Com2Transfer = 837, Com2Outer = 842, Com2Inner = 843;
    public const uint Nav1Transfer = 729, Nav1Inner = 732, Nav1Outer = 733;
    public const uint Nav2Transfer = 845, Nav2Outer = 848, Nav2Inner = 849;
    public const uint XpdrKnob1 = 804, XpdrKnob2 = 805, XpdrKnob3 = 807, XpdrKnob4 = 808; // EVT_TCAS_KNOB1..4
    public const uint CaptainBaro = 365, CaptainBaroStd = 367;                         // EVT_EFIS_CPT_BARO(_STD)

    /// <summary>Every event RadioBridge sends, for mapping them once per connection.</summary>
    public static readonly uint[] Events =
    [
        Com1Transfer, Com1Outer, Com1Inner, Com2Transfer, Com2Outer, Com2Inner,
        Nav1Transfer, Nav1Inner, Nav1Outer, Nav2Transfer, Nav2Outer, Nav2Inner,
        XpdrKnob1, XpdrKnob2, XpdrKnob3, XpdrKnob4, CaptainBaro, CaptainBaroStd,
    ];

    /// <summary>The 737's transponder digit knobs, first digit first.</summary>
    private static readonly uint[] XpdrDigitKnobs = [XpdrKnob1, XpdrKnob2, XpdrKnob3, XpdrKnob4];

    /// <summary>
    /// The loaded aircraft is a PMDG 737, going by the path of its .cfg (its folder is "PMDG 737-800" and the like)
    /// or its title. The title alone isn't enough: liveries are titled like "737-800 PAX BW TC".
    /// </summary>
    public static bool IsPmdg737(string? aircraftPathOrTitle) =>
        aircraftPathOrTitle is not null
        && aircraftPathOrTitle.Contains("PMDG", StringComparison.OrdinalIgnoreCase)
        && aircraftPathOrTitle.Contains("737", StringComparison.Ordinal);

    /// <summary>
    /// The PMDG events for a panel command, in order, as (event number, parameter); empty when the 737 has
    /// nothing for it (the ADF is left alone: its three knobs and standby don't match the panel's ADF).
    /// </summary>
    public static IReadOnlyList<(uint Event, uint Parameter)> Translate(SimCommand command, RadioState state) => command switch
    {
        SwapRadio c => Click(c.Radio switch
        {
            Tunable.Com1 => Com1Transfer,
            Tunable.Com2 => Com2Transfer,
            Tunable.Nav1 => Nav1Transfer,
            _ => Nav2Transfer,
        }),
        TurnKnobs { Target: KnobTarget.Com1 } c => Turn(c.Outer ? Com1Outer : Com1Inner, c.Steps),
        TurnKnobs { Target: KnobTarget.Com2 } c => Turn(c.Outer ? Com2Outer : Com2Inner, c.Steps),
        TurnKnobs { Target: KnobTarget.Nav1 } c => Turn(c.Outer ? Nav1Outer : Nav1Inner, c.Steps),
        TurnKnobs { Target: KnobTarget.Nav2 } c => Turn(c.Outer ? Nav2Outer : Nav2Inner, c.Steps),
        // One baro knob: a detent is 1 hPa or 0.01 inHg (whichever the EFIS shows), the panel's outer knob 10 of them.
        TurnKnobs { Target: KnobTarget.Baro } c => Turn(CaptainBaro, c.Steps * (c.Outer ? 10 : 1)),
        TurnKnobs { Target: KnobTarget.Squawk } c => SquawkDigits((int)c.From, (int)c.To),
        // STD is a toggle: pressing it while on standard would take it off again.
        SetBaroStandard when Math.Abs(state.BaroHpa - 1013.25) > 0.3 => Click(CaptainBaroStd),
        _ => [],
    };

    /// <summary>A knob turned <paramref name="steps"/> detents: wheel up is clockwise.</summary>
    private static List<(uint, uint)> Turn(uint knob, int steps) =>
        Enumerable.Repeat((knob, steps > 0 ? MouseWheelUp : MouseWheelDown), Math.Abs(steps)).ToList();

    private static List<(uint, uint)> Click(uint control) => [(control, MouseLeftSingle), (control, MouseLeftRelease)];

    /// <summary>
    /// The 737 sets each squawk digit with its own knob (0-7, wrapping), where the panel steps two digits at a time:
    /// each digit knob goes the shorter way from the old digit to the new one.
    /// </summary>
    internal static List<(uint, uint)> SquawkDigits(int from, int to)
    {
        var events = new List<(uint, uint)>();
        if (from < 0 || to < 0)
            return events;
        for (int digit = 0; digit < 4; digit++)
        {
            int divisor = (int)Math.Pow(10, 3 - digit);
            int up = ((to / divisor % 10) - (from / divisor % 10) + 8) % 8;
            events.AddRange(Turn(XpdrDigitKnobs[digit], up <= 4 ? up : up - 8));
        }
        return events;
    }
}
