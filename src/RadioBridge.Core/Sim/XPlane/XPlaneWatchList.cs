using System.Text.RegularExpressions;

namespace RadioBridge.Core.Sim.XPlane;

/// <summary>
/// Which datarefs a debug recording watches besides the ones RadioBridge uses: the rest of X-Plane's radio,
/// altimeter and avionics-power datarefs, the loaded aircraft, and whatever the aircraft and its add-ons
/// publish about radios. Add-ons often run their own radios (REP's Bendix King stack, a GTN, a custom
/// altimeter), and their datarefs show what the cockpit really does with what RadioBridge sends.
/// Instrument outputs that change all the time in flight (needles, deviation dots, timers) are left out.
/// </summary>
public static partial class XPlaneWatchList
{
    /// <summary>A sanity cap; a typical aircraft gives a few hundred.</summary>
    public const int MaxCount = 1500;

    [GeneratedRegex(@"^sim/(cockpit2/radios/actuators/|cockpit/radios/|cockpit2/gauges/actuators/baro|cockpit/misc/baro|cockpit2/electrical/avionics|cockpit/electrical/avionics|aircraft/view/acf_(relative_path|ui_name|ICAO|author)$)")]
    private static partial Regex XPlaneOwn();

    // Words that mark a radio-related dataref in an add-on's namespace, as a whole path or name part.
    [GeneratedRegex(@"(^|[/_])(com\d?|nav\d?|adf\d?|xpdr|transponder|baro\w*|radios?|freq\w*|kx\d+\w*|kr\d+|kt\d+|kma\d+|gtx\d*|gtn\w*|gns\w*|altimeter\w*|kollsman|qnh|squawk|swap|flip|stby|standby)($|[/_\d])", RegexOptions.IgnoreCase)]
    private static partial Regex RadioWords();

    // Kept even though they live among instrument outputs: which GTN units are installed and running.
    [GeneratedRegex(@"gtnxi_(unit|type)\d*$", RegexOptions.IgnoreCase)]
    private static partial Regex AlwaysWanted();

    // Continuously changing instrument outputs and audio plumbing: noise in a recording about radio settings.
    [GeneratedRegex(@"(indicators?/|hsi|_dots|bearing|cross_track|_eta|_time|speed|deflection|brightness|spill|load_ratio|volume|_led$|lites|_rx$|_tx|rx_status|tx_status|receiving|callsign|ptt|selcal|headset|mute)", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    /// <summary>Picks the datarefs to watch from X-Plane's full list (name and value_type), leaving out <paramref name="already"/>.</summary>
    public static IReadOnlyList<string> Select(IEnumerable<(string Name, string Type)> datarefs, IEnumerable<string> already)
    {
        var skip = already.ToHashSet(StringComparer.Ordinal);
        return datarefs
            .Where(d => !skip.Contains(d.Name) && Wanted(d.Name, d.Type))
            .Select(d => d.Name)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxCount)
            .ToList();
    }

    internal static bool Wanted(string name, string type)
    {
        // Byte data is only interesting for the aircraft's identity; elsewhere it is mostly display text.
        if (type == "data" && !name.StartsWith("sim/aircraft/view/acf_", StringComparison.Ordinal))
            return false;
        if (AlwaysWanted().IsMatch(name))
            return true;
        if (Noise().IsMatch(name))
            return false;
        return name.StartsWith("sim/", StringComparison.Ordinal)
            ? XPlaneOwn().IsMatch(name)
            : RadioWords().IsMatch(name);
    }
}
