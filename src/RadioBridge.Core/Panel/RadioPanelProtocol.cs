namespace RadioBridge.Core.Panel;

/// <summary>
/// Wire format of the Logitech/Saitek Pro Flight Radio Panel (USB 06A3:0D05).
/// <para>
/// Input is a 3-byte report read as a little-endian 24-bit field:
/// bits 0-6 upper selector (COM1..XPDR, one-hot), bits 7-13 lower selector, bit 14/15 upper/lower
/// ACT/STBY, bits 16-19 upper knob (inner CW, inner CCW, outer CW, outer CCW), bits 20-23 lower knob.
/// Each knob bit pulses high for one detent.
/// </para>
/// <para>
/// Output is a 22-byte feature report (report id 0): upper-left, upper-right, lower-left and
/// lower-right displays, 5 digits each, then two padding bytes. A digit byte is 0-9, 0x0F for blank,
/// 0xD0 | n for digit n with its decimal point lit, or 0xEF for a dash.
/// </para>
/// </summary>
public static class RadioPanelProtocol
{
    public const int VendorId = 0x06A3;
    public const int ProductId = 0x0D05;
    public const int InputReportLength = 3;
    public const int DisplayReportLength = 22;
    public const int DigitsPerDisplay = 5;

    private const byte BlankCell = 0x0F;
    private const byte PointFlag = 0xD0;
    private const byte DashCell = 0xEF;

    private const int UpperSelectorShift = 0;
    private const int LowerSelectorShift = 7;
    private const int UpperButtonBit = 14;
    private const int LowerButtonBit = 15;
    private const int UpperKnobShift = 16;
    private const int LowerKnobShift = 20;

    /// <summary>
    /// Builds the display report. Each text is right-aligned in its 5 digits: '0'-'9' are digits,
    /// '.' lights the decimal point of the preceding digit, ' ' is blank and '-' is a dash. Text longer
    /// than 5 digits keeps its rightmost digits. Other characters are ignored.
    /// </summary>
    public static byte[] EncodeDisplays(PanelDisplays displays)
    {
        var report = new byte[DisplayReportLength];
        EncodeDisplay(displays.UpperLeft, report.AsSpan(0, DigitsPerDisplay));
        EncodeDisplay(displays.UpperRight, report.AsSpan(5, DigitsPerDisplay));
        EncodeDisplay(displays.LowerLeft, report.AsSpan(10, DigitsPerDisplay));
        EncodeDisplay(displays.LowerRight, report.AsSpan(15, DigitsPerDisplay));
        return report;
    }

    internal static void EncodeDisplay(string? text, Span<byte> cells)
    {
        cells.Fill(BlankCell);
        if (string.IsNullOrEmpty(text))
            return;

        var parsed = new List<byte>(text.Length);
        foreach (char c in text)
        {
            switch (c)
            {
                case >= '0' and <= '9':
                    parsed.Add((byte)(c - '0'));
                    break;
                case '.':
                    // A point belongs to the digit before it; a stray point has nothing to attach to.
                    if (parsed.Count > 0 && parsed[^1] <= 9)
                        parsed[^1] |= PointFlag;
                    break;
                case ' ':
                    parsed.Add(BlankCell);
                    break;
                case '-':
                    parsed.Add(DashCell);
                    break;
            }
        }

        int take = Math.Min(parsed.Count, cells.Length);
        for (int i = 0; i < take; i++)
            cells[cells.Length - take + i] = parsed[parsed.Count - take + i];
    }

    /// <summary>Packs a raw input report (without the report-id byte) into its 24-bit state.</summary>
    public static int ReadState(ReadOnlySpan<byte> report) => report[0] | report[1] << 8 | report[2] << 16;

    internal static Selector? SelectorOf(int state, PanelRow row)
    {
        int bits = (state >> (row == PanelRow.Upper ? UpperSelectorShift : LowerSelectorShift)) & 0x7F;
        for (int i = 0; i < 7; i++)
        {
            if ((bits & (1 << i)) != 0)
                return (Selector)i;
        }
        return null; // between detents while the selector is moving
    }

    internal static bool ButtonOf(int state, PanelRow row) =>
        (state & (1 << (row == PanelRow.Upper ? UpperButtonBit : LowerButtonBit))) != 0;

    /// <summary>Bit index of a knob direction: inner CW, inner CCW, outer CW, outer CCW per row.</summary>
    internal static int KnobBit(PanelRow row, Knob knob, int direction) =>
        (row == PanelRow.Upper ? UpperKnobShift : LowerKnobShift)
        + (knob == Knob.Inner ? 0 : 2)
        + (direction > 0 ? 0 : 1);
}
