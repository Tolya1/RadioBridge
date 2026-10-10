using System.Globalization;

namespace RadioBridge.Core.Radio;

public enum BaroUnit
{
    Hectopascal,
    InchesOfMercury,
}

/// <summary>Formats values for the panel's 5-digit displays (see RadioPanelProtocol for the syntax).</summary>
public static class DisplayFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public const double HpaPerInHg = 33.8639;

    /// <summary>
    /// COM channel. Compact drops the leading '1' every COM frequency has and keeps all three decimals
    /// (123.405 -> "23.405"), so 8.33 kHz channels fit. Classic shows "123.40".
    /// </summary>
    public static string Com(int khz, bool compact)
    {
        if (khz <= 0)
            return "";
        return compact
            ? (khz % 100_000 / 1000.0).ToString("00.000", Inv)
            : $"{khz / 1000}.{khz % 1000 / 10:00}";
    }

    /// <summary>NAV frequency, always "110.30" (NAV channels are 50 kHz apart).</summary>
    public static string Nav(int khz) => khz <= 0 ? "" : $"{khz / 1000}.{khz % 1000 / 10:00}";

    public static string Adf(int khz) => khz <= 0 ? "" : khz.ToString(Inv);

    /// <summary>DME distance with one decimal; blank when there is no station.</summary>
    public static string Dme(double nm) =>
        double.IsNaN(nm) || nm <= 0 ? "" : Math.Min(nm, 999.9).ToString("0.0", Inv);

    public static string Baro(double hpa, BaroUnit unit)
    {
        if (double.IsNaN(hpa) || hpa <= 0)
            return "";
        return unit == BaroUnit.InchesOfMercury
            ? (hpa / HpaPerInHg).ToString("00.00", Inv)
            : Math.Round(hpa).ToString("0", Inv);
    }

    public static string Squawk(int code) => code < 0 ? "" : code.ToString("0000", Inv);

    /// <summary>Marks the value the knobs currently edit by lighting the last decimal point.</summary>
    public static string WithFocus(string text) => text.Length == 0 ? text : text + ".";
}
