using RadioBridge.Core.Panel;

namespace RadioBridge.Tests;

public class PanelProtocolTests
{
    private static byte[] Encode(string text)
    {
        var cells = new byte[5];
        RadioPanelProtocol.EncodeDisplay(text, cells);
        return cells;
    }

    [Fact]
    public void Digits_are_right_aligned_with_blank_padding()
    {
        Assert.Equal(new byte[] { 0x0F, 0x01, 0x02, 0x00, 0x00 }, Encode("1200"));
        Assert.Equal(new byte[] { 0x0F, 0x0F, 0x0F, 0x0F, 0x0F }, Encode(""));
    }

    [Fact]
    public void Point_lights_on_the_preceding_digit()
    {
        Assert.Equal(new byte[] { 0x02, 0xD3, 0x04, 0x00, 0x05 }, Encode("23.405"));
        Assert.Equal(new byte[] { 0x01, 0x01, 0xD8, 0x03, 0x00 }, Encode("118.30"));
        Assert.Equal(new byte[] { 0x0F, 0x01, 0x02, 0x00, 0xD0 }, Encode("1200."));
    }

    [Fact]
    public void Too_long_text_keeps_the_rightmost_digits()
    {
        Assert.Equal(Encode("23.405"), Encode("123.405"));
    }

    [Fact]
    public void Dash_and_space_are_supported()
    {
        Assert.Equal(new byte[] { 0x0F, 0xEF, 0x0F, 0xEF, 0x0F }, Encode("- - "));
    }

    [Fact]
    public void Report_holds_four_displays_in_panel_order()
    {
        var report = RadioPanelProtocol.EncodeDisplays(new PanelDisplays("1", "2", "3", "4"));

        Assert.Equal(22, report.Length);
        Assert.Equal(0x01, report[4]);
        Assert.Equal(0x02, report[9]);
        Assert.Equal(0x03, report[14]);
        Assert.Equal(0x04, report[19]);
        Assert.Equal(0x00, report[20]);
        Assert.Equal(0x00, report[21]);
    }
}

public class PanelDecoderTests
{
    private static byte[] Report(int bits) => [(byte)bits, (byte)(bits >> 8), (byte)(bits >> 16)];

    private const int UpperCom1 = 1 << 0;
    private const int UpperXpdr = 1 << 6;
    private const int LowerNav1 = 1 << 9;
    private const int UpperButton = 1 << 14;
    private const int LowerButton = 1 << 15;
    private const int UpperInnerCw = 1 << 16;
    private const int UpperOuterCcw = 1 << 19;
    private const int LowerInnerCcw = 1 << 21;

    [Fact]
    public void First_report_sets_both_selectors_only()
    {
        var decoder = new RadioPanelDecoder();

        var events = decoder.Decode(Report(UpperCom1 | LowerNav1 | UpperInnerCw));

        Assert.Equal(
            new PanelEvent[] { new SelectorMoved(PanelRow.Upper, Selector.Com1), new SelectorMoved(PanelRow.Lower, Selector.Nav1) },
            events);
    }

    [Fact]
    public void Selector_reports_only_changes_and_ignores_the_gap_between_detents()
    {
        var decoder = new RadioPanelDecoder();
        decoder.Decode(Report(UpperCom1 | LowerNav1));

        Assert.Empty(decoder.Decode(Report(LowerNav1))); // upper selector between positions
        Assert.Equal(new PanelEvent[] { new SelectorMoved(PanelRow.Upper, Selector.Xpdr) }, decoder.Decode(Report(UpperXpdr | LowerNav1)));
    }

    [Fact]
    public void Knob_detent_fires_on_rising_edge()
    {
        var decoder = new RadioPanelDecoder();
        int baseBits = UpperCom1 | LowerNav1;
        decoder.Decode(Report(baseBits));

        Assert.Equal(new PanelEvent[] { new KnobTurned(PanelRow.Upper, Knob.Inner, 1) }, decoder.Decode(Report(baseBits | UpperInnerCw)));
        Assert.Empty(decoder.Decode(Report(baseBits | UpperInnerCw | UpperButton)).OfType<KnobTurned>());
        decoder.Decode(Report(baseBits));
        Assert.Equal(new PanelEvent[] { new KnobTurned(PanelRow.Upper, Knob.Outer, -1) }, decoder.Decode(Report(baseBits | UpperOuterCcw)));
        Assert.Equal(new PanelEvent[] { new KnobTurned(PanelRow.Lower, Knob.Inner, -1) }, decoder.Decode(Report(baseBits | LowerInnerCcw)));
    }

    [Fact]
    public void Buttons_report_press_and_release()
    {
        var decoder = new RadioPanelDecoder();
        int baseBits = UpperCom1 | LowerNav1;
        decoder.Decode(Report(baseBits));

        Assert.Equal(new PanelEvent[] { new ButtonChanged(PanelRow.Lower, true) }, decoder.Decode(Report(baseBits | LowerButton)));
        Assert.Equal(new PanelEvent[] { new ButtonChanged(PanelRow.Lower, false) }, decoder.Decode(Report(baseBits)));
    }
}
