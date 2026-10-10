namespace RadioBridge.Core.Radio;

public enum ComSpacing
{
    /// <summary>8.33 kHz channels (118.000, 118.005, 118.010, 118.015, 118.025, ...).</summary>
    Khz833,

    /// <summary>Classic 25 kHz channels (118.000, 118.025, ...).</summary>
    Khz25,
}

/// <summary>
/// Frequency arithmetic in whole kHz (118.005 MHz = 118005), which keeps 8.33 kHz channel names exact.
/// Inner-knob steps wrap inside the current MHz and outer-knob steps wrap inside the band, the way
/// a real radio's concentric knobs behave.
/// </summary>
public static class Frequencies
{
    public const int ComFirstMhz = 118;
    public const int ComLastMhz = 136;
    public const int NavFirstMhz = 108;
    public const int NavLastMhz = 117;
    public const int NavStepKhz = 50;

    // In every 25 kHz block the 8.33 channel names end in 00, 05, 10 and 15 (20 is never used).
    private const int ChannelsPerBlock = 4;
    private const int BlocksPerMhz = 40;

    /// <summary>
    /// Maps a COM frequency to its channel name in kHz. Accepts either the channel name (118.010) or the
    /// real 8.33 frequency behind it (118.00833), and repairs a 25 kHz value truncated to 10 kHz
    /// resolution (118.020 -> 118.025).
    /// </summary>
    public static int ComChannelKhz(double mhz)
    {
        int khz = (int)Math.Round(mhz * 1000 / 5, MidpointRounding.AwayFromZero) * 5;
        return khz % 25 == 20 ? khz + 5 : khz;
    }

    /// <summary>NAV frequency in kHz on the 50 kHz grid.</summary>
    public static int NavKhz(double mhz) => (int)Math.Round(mhz * 1000 / NavStepKhz, MidpointRounding.AwayFromZero) * NavStepKhz;

    public static bool IsComChannel(int khz, ComSpacing spacing) =>
        spacing == ComSpacing.Khz25 ? khz % 25 == 0 : khz % 25 is 0 or 5 or 10 or 15;

    /// <summary>Inner knob: moves <paramref name="channels"/> channels, wrapping inside the MHz.</summary>
    public static int StepComChannel(int khz, int channels, ComSpacing spacing)
    {
        if (channels == 0)
            return khz;

        int mhz = khz / 1000;
        int sub = khz % 1000;
        int perBlock = spacing == ComSpacing.Khz25 ? 1 : ChannelsPerBlock;

        // Highest channel at or below the value. From a value between channels (e.g. 118.015 in 25 kHz
        // mode) the first step lands on the neighbouring channel in the direction of travel.
        int floor = sub / 25 * perBlock + Math.Min(sub % 25 / 5, perBlock - 1);
        int target = IsComChannel(khz, spacing) || channels > 0 ? floor + channels : floor + channels + 1;

        int position = Mod(target, BlocksPerMhz * perBlock);
        return mhz * 1000 + position / perBlock * 25 + position % perBlock * 5;
    }

    /// <summary>Outer knob on COM: moves whole MHz, wrapping 118 &lt;-&gt; 136.</summary>
    public static int StepComMhz(int khz, int mhzSteps) =>
        WrapMhz(khz, mhzSteps, ComFirstMhz, ComLastMhz);

    /// <summary>Inner knob on NAV: 50 kHz steps, wrapping inside the MHz.</summary>
    public static int StepNavChannel(int khz, int steps)
    {
        int mhz = khz / 1000;
        int index = Mod((khz % 1000) / NavStepKhz + steps, 1000 / NavStepKhz);
        return mhz * 1000 + index * NavStepKhz;
    }

    /// <summary>Outer knob on NAV: whole MHz, wrapping 108 &lt;-&gt; 117.</summary>
    public static int StepNavMhz(int khz, int mhzSteps) =>
        WrapMhz(khz, mhzSteps, NavFirstMhz, NavLastMhz);

    private static int WrapMhz(int khz, int mhzSteps, int first, int last)
    {
        int mhz = Math.Clamp(khz / 1000, first, last);
        int stepped = first + Mod(mhz - first + mhzSteps, last - first + 1);
        return stepped * 1000 + khz % 1000;
    }

    internal static int Mod(int value, int modulus) => ((value % modulus) + modulus) % modulus;
}
