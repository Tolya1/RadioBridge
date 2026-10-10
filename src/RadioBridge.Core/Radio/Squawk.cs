namespace RadioBridge.Core.Radio;

/// <summary>
/// Transponder codes as four octal digits written in decimal (7700 = "7700"). The outer knob edits
/// the first pair of digits and the inner knob the last pair, each pair wrapping 00-77.
/// </summary>
public static class Squawk
{
    public const int Vfr = 1200;

    public static int StepHighPair(int code, int steps) => Combine(Mod64(High(code) + steps), Low(code));

    public static int StepLowPair(int code, int steps) => Combine(High(code), Mod64(Low(code) + steps));

    public static bool IsValid(int code) =>
        code is >= 0 and <= 7777 && code.ToString("0000").All(c => c <= '7');

    /// <summary>Binary-coded octal (one digit per nibble), as MSFS uses: 1200 -> 0x1200.</summary>
    public static uint ToBco16(int code)
    {
        uint result = 0;
        for (int shift = 0; shift < 16; shift += 4, code /= 10)
            result |= (uint)(code % 10) << shift;
        return result;
    }

    public static int FromBco16(uint bco) =>
        (int)((bco >> 12 & 0xF) * 1000 + (bco >> 8 & 0xF) * 100 + (bco >> 4 & 0xF) * 10 + (bco & 0xF));

    // A pair is two octal digits, i.e. a value 0-63.
    private static int High(int code) => (code / 1000 % 10 & 7) * 8 + (code / 100 % 10 & 7);
    private static int Low(int code) => (code / 10 % 10 & 7) * 8 + (code % 10 & 7);
    private static int Combine(int high, int low) => high / 8 * 1000 + high % 8 * 100 + low / 8 * 10 + low % 8;
    private static int Mod64(int value) => Frequencies.Mod(value, 64);
}
