using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace RadioBridge.Core.Sim.Msfs;

/// <summary>One SimConnect_SetInputEvent call: the input event and the values it is set to.</summary>
internal sealed record InputEventSet(string Name, ulong Hash, double[] Values);

/// <summary>
/// The GNS 430/530 keys of the loaded MSFS aircraft, as input events (B: events). SimConnect can't fire the
/// GNS's own H: events (<c>AS530_ENT_Push</c> and so on), but aircraft built on Asobo's stock GNS behaviour
/// (ModelBehaviorDefs\Asobo\GPS\AS430.xml) expose an input event per key and knob that does: named
/// <c>&lt;unit&gt;_&lt;control&gt;</c>, e.g. <c>AS530_ENT</c>, <c>AS430_GPS_Inner</c>, or <c>AS530_1_ENT</c> where an
/// aircraft numbers two 530s. Names compare case-insensitively (MSFS lists them in capitals).
/// <para>
/// What a value does comes from that template: a button fires on 1; a knob turns clockwise for a value above
/// 0, otherwise anticlockwise (one detent per set); the range goes up (map zoomed out) above 0, down otherwise;
/// CLR takes a press and a release, and counts as held when they are 2 s of sim time apart, or when pressed
/// with a duration of 2 (its own "Push_Long" binding).
/// </para>
/// </summary>
internal sealed partial class GnsInputEvents
{
    public static readonly GnsInputEvents None = new([]);

    // The stock template's unit names: the 530 and the 430, optionally numbered.
    [GeneratedRegex(@"^(AS[45]30(?:_(\d+))?)_ENT$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnitEnter();

    private readonly List<string> _prefixes;
    private readonly Dictionary<string, (string Name, ulong Hash)> _events;

    public GnsInputEvents(IEnumerable<(string Name, ulong Hash)> events)
    {
        _events = new Dictionary<string, (string, ulong)>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in events)
            _events.TryAdd(e.Name, e); // MSFS 2024 can list an event twice
        // GPS 1 is the first unit: the lowest number, else the 530 (on top in a 530 + 430 stack).
        _prefixes = _events.Keys
            .Select(name => UnitEnter().Match(name))
            .Where(m => m.Success)
            .OrderBy(m => m.Groups[2].Success ? int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 0)
            .ThenBy(m => m.Groups[1].Value.StartsWith("AS530", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Select(m => m.Groups[1].Value)
            .ToList();
    }

    /// <summary>The units found, GPS 1 first (e.g. "AS530", "AS430").</summary>
    public IReadOnlyList<string> Units => _prefixes;

    /// <summary>The input event sets that make <paramref name="key"/> happen on GPS <paramref name="unit"/>; empty when it has no such control.</summary>
    public IReadOnlyList<InputEventSet> Sets(int unit, GpsKey key)
    {
        if (unit < 1 || unit > _prefixes.Count)
            return [];
        var (control, values) = Control(key);
        return _events.TryGetValue($"{_prefixes[unit - 1]}_{control}", out var e)
            ? values.Select(v => new InputEventSet(e.Name, e.Hash, v)).ToList()
            : [];
    }

    private static (string Control, double[][] Values) Control(GpsKey key) => key switch
    {
        GpsKey.LargeKnobUp => ("GPS_Outer", [[1]]),
        GpsKey.LargeKnobDown => ("GPS_Outer", [[-1]]),
        GpsKey.SmallKnobUp => ("GPS_Inner", [[1]]),
        GpsKey.SmallKnobDown => ("GPS_Inner", [[-1]]),
        GpsKey.Cursor => ("CRSR", [[1]]), // pushes the small knob
        GpsKey.Enter => ("ENT", [[1]]),
        GpsKey.Clear => ("CLR", [[1, 0], [0, 0]]), // press and release at once: a short press
        GpsKey.ClearHeld => ("CLR", [[1, 2]]),     // pressed for 2 s
        GpsKey.DirectTo => ("DirectTo", [[1]]),
        GpsKey.Menu => ("MENU", [[1]]),
        GpsKey.FlightPlan => ("FPL", [[1]]),
        GpsKey.Procedure => ("PROC", [[1]]),
        GpsKey.Cdi => ("CDI", [[1]]),
        GpsKey.Obs => ("OBS", [[1]]),
        GpsKey.Message => ("MSG", [[1]]),
        GpsKey.Vnav => ("VNAV", [[1]]),
        GpsKey.ZoomIn => ("Range_Zoom", [[-1]]),
        _ => ("Range_Zoom", [[1]]), // ZoomOut: a larger range
    };

    /// <summary>The size of SIMCONNECT_RECV_ENUMERATE_INPUT_EVENTS before its descriptors.</summary>
    internal const int ListHeader = 28;

    /// <summary>A descriptor: char Name[64], UINT64 Hash, DWORD eType (packed, 76 bytes).</summary>
    internal const int DescriptorSize = 76;

    /// <summary>
    /// Reads one SIMCONNECT_RECV_ENUMERATE_INPUT_EVENTS packet of <paramref name="size"/> bytes: its request,
    /// which page of how many it is, and its events. The descriptor stride is taken from the packet size, as
    /// the 2024 documentation and headers disagree about the descriptor's fields.
    /// </summary>
    public static (uint Request, uint Page, uint Pages, List<(string Name, ulong Hash)> Events) ReadPage(IntPtr data, uint size)
    {
        uint request = (uint)Marshal.ReadInt32(data, 12);
        uint count = (uint)Marshal.ReadInt32(data, 16);
        uint page = (uint)Marshal.ReadInt32(data, 20);
        uint pages = (uint)Marshal.ReadInt32(data, 24);
        var events = new List<(string, ulong)>();
        if (count > 0 && size > ListHeader)
        {
            long stride = (size - ListHeader) / count;
            if (stride >= DescriptorSize)
            {
                var name = new byte[64];
                for (int i = 0; i < count; i++)
                {
                    var descriptor = IntPtr.Add(data, (int)(ListHeader + i * stride));
                    Marshal.Copy(descriptor, name, 0, name.Length);
                    int length = Array.IndexOf(name, (byte)0) is var end and >= 0 ? end : name.Length;
                    events.Add((Encoding.UTF8.GetString(name, 0, length), (ulong)Marshal.ReadInt64(descriptor, 64)));
                }
            }
        }
        return (request, page, pages, events);
    }
}
