using RadioBridge.Core.Radio;
using RadioBridge.Core.Sim.Msfs;

namespace RadioBridge.Tests;

public class FrequenciesTests
{
    [Theory]
    [InlineData(118_000, 1, 118_005)]
    [InlineData(118_015, 1, 118_025)] // there is no 118.020 channel
    [InlineData(118_025, -1, 118_015)]
    [InlineData(118_090, 1, 118_100)]
    [InlineData(118_990, 1, 118_000)] // inner knob wraps inside the MHz
    [InlineData(118_000, -1, 118_990)]
    [InlineData(122_800, 8, 122_850)] // 4 channels per 25 kHz block
    public void Steps_833_channels(int from, int steps, int expected) =>
        Assert.Equal(expected, Frequencies.StepComChannel(from, steps, ComSpacing.Khz833));

    [Theory]
    [InlineData(118_000, 1, 118_025)]
    [InlineData(118_975, 1, 118_000)]
    [InlineData(118_015, 1, 118_025)]  // off-grid: first step lands on the next channel up
    [InlineData(118_015, -1, 118_000)] // ...or down
    [InlineData(118_005, 2, 118_050)]
    public void Steps_25_khz_channels(int from, int steps, int expected) =>
        Assert.Equal(expected, Frequencies.StepComChannel(from, steps, ComSpacing.Khz25));

    [Fact]
    public void Repairs_an_invalid_833_value_in_the_direction_of_travel()
    {
        Assert.Equal(118_025, Frequencies.StepComChannel(118_020, 1, ComSpacing.Khz833));
        Assert.Equal(118_015, Frequencies.StepComChannel(118_020, -1, ComSpacing.Khz833));
    }

    [Theory]
    [InlineData(118_005, 1, 119_005)]
    [InlineData(136_990, 1, 118_990)]
    [InlineData(118_250, -1, 136_250)]
    public void Steps_com_mhz_with_band_wrap(int from, int steps, int expected) =>
        Assert.Equal(expected, Frequencies.StepComMhz(from, steps));

    [Theory]
    [InlineData(110_300, 1, 110_350)]
    [InlineData(110_950, 1, 110_000)]
    public void Steps_nav_channels(int from, int steps, int expected) =>
        Assert.Equal(expected, Frequencies.StepNavChannel(from, steps));

    [Theory]
    [InlineData(117_000, 1, 108_000)]
    [InlineData(108_500, -1, 117_500)]
    public void Steps_nav_mhz_with_band_wrap(int from, int steps, int expected) =>
        Assert.Equal(expected, Frequencies.StepNavMhz(from, steps));

    [Theory]
    [InlineData(118.010, 118_010)]
    [InlineData(118.008333, 118_010)] // real 8.33 frequency of channel 118.010
    [InlineData(118.016667, 118_015)]
    [InlineData(118.033333, 118_035)]
    [InlineData(118.025, 118_025)]
    [InlineData(118.020, 118_025)]    // 25 kHz channel truncated to 10 kHz resolution
    [InlineData(122.79999, 122_800)]  // float noise
    public void Normalizes_com_values_to_channel_names(double mhz, int expected) =>
        Assert.Equal(expected, Frequencies.ComChannelKhz(mhz));
}

public class SquawkTests
{
    [Theory]
    [InlineData(1200, 1, 1201)]
    [InlineData(1277, 1, 1200)] // low pair wraps 77 -> 00 without carrying
    [InlineData(1200, -1, 1277)]
    [InlineData(1207, 1, 1210)]
    public void Inner_knob_steps_the_last_two_digits(int from, int steps, int expected) =>
        Assert.Equal(expected, Squawk.StepLowPair(from, steps));

    [Theory]
    [InlineData(1200, 1, 1300)]
    [InlineData(7700, 1, 0000)]
    [InlineData(0045, -1, 7745)]
    public void Outer_knob_steps_the_first_two_digits(int from, int steps, int expected) =>
        Assert.Equal(expected, Squawk.StepHighPair(from, steps));

    [Fact]
    public void Bco16_round_trips()
    {
        Assert.Equal(0x7700u, Squawk.ToBco16(7700));
        Assert.Equal(1200, Squawk.FromBco16(0x1200));
    }

    [Fact]
    public void Adf_bcd_is_one_digit_per_nibble() => Assert.Equal(0x00362000u, MsfsBackend.ToBcd(362_000));
}

public class DisplayFormatTests
{
    [Theory]
    [InlineData(123_405, true, "23.405")]
    [InlineData(118_000, true, "18.000")]
    [InlineData(136_990, true, "36.990")]
    [InlineData(123_405, false, "123.40")]
    [InlineData(0, true, "")]
    public void Com(int khz, bool compact, string expected) => Assert.Equal(expected, DisplayFormat.Com(khz, compact));

    [Fact]
    public void Nav() => Assert.Equal("110.30", DisplayFormat.Nav(110_300));

    [Theory]
    [InlineData(1013.25, BaroUnit.Hectopascal, "1013")]
    [InlineData(1013.25, BaroUnit.InchesOfMercury, "29.92")]
    [InlineData(double.NaN, BaroUnit.Hectopascal, "")]
    public void Baro(double hpa, BaroUnit unit, string expected) => Assert.Equal(expected, DisplayFormat.Baro(hpa, unit));

    [Theory]
    [InlineData(12.34, "12.3")]
    [InlineData(0, "")]
    [InlineData(double.NaN, "")]
    public void Dme(double nm, string expected) => Assert.Equal(expected, DisplayFormat.Dme(nm));
}
