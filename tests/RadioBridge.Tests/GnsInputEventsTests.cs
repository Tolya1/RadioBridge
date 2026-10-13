using System.Runtime.InteropServices;
using System.Text;
using RadioBridge.Core.Sim;
using RadioBridge.Core.Sim.Msfs;

namespace RadioBridge.Tests;

/// <summary>The GNS 430/530 keys of an MSFS aircraft, as the stock GNS behaviour's input events.</summary>
public sealed class GnsInputEventsTests
{
    // As MSFS lists them: in capitals, among the aircraft's other input events.
    private static readonly (string, ulong)[] Baron =
    [
        ("LIGHTING_PANEL_1", 1), ("AS530_ENT", 11), ("AS530_CLR", 12), ("AS530_CRSR", 13), ("AS530_GPS_OUTER", 14),
        ("AS530_GPS_INNER", 15), ("AS530_DIRECTTO", 16), ("AS530_FPL", 17), ("AS530_RANGE_ZOOM", 18), ("AS530_VNAV", 19),
        ("AS430_ENT", 21), ("AS430_CLR", 22), ("AS430_DIRECTTO", 26), ("AS430_GPS_INNER", 25),
    ];

    [Fact]
    public void The_530_is_gps_1_and_the_430_gps_2()
    {
        var gns = new GnsInputEvents(Baron.Reverse());

        Assert.Equal(["AS530", "AS430"], gns.Units);
        var enter = gns.Sets(1, GpsKey.Enter).Single();
        Assert.Equal(("AS530_ENT", 11ul, 1.0), (enter.Name, enter.Hash, enter.Values.Single()));
        var direct = gns.Sets(2, GpsKey.DirectTo).Single();
        Assert.Equal(("AS430_DIRECTTO", 26ul, 1.0), (direct.Name, direct.Hash, direct.Values.Single()));
    }

    [Fact]
    public void Numbered_units_go_by_their_number()
    {
        var gns = new GnsInputEvents([("AS530_2_ENT", 2), ("AS530_1_ENT", 1)]);

        Assert.Equal(["AS530_1", "AS530_2"], gns.Units);
        Assert.Equal(2ul, gns.Sets(2, GpsKey.Enter).Single().Hash);
    }

    [Fact]
    public void Knobs_turn_one_detent_per_set_clockwise_above_zero()
    {
        var gns = new GnsInputEvents(Baron);

        Assert.Equal([1.0], gns.Sets(1, GpsKey.LargeKnobUp).Single().Values);
        Assert.Equal([-1.0], gns.Sets(1, GpsKey.LargeKnobDown).Single().Values);
        Assert.Equal("AS530_GPS_INNER", gns.Sets(1, GpsKey.SmallKnobUp).Single().Name);
        Assert.Equal([-1.0], gns.Sets(1, GpsKey.SmallKnobDown).Single().Values);
        Assert.Equal([1.0], gns.Sets(1, GpsKey.ZoomOut).Single().Values); // a larger range
        Assert.Equal([-1.0], gns.Sets(1, GpsKey.ZoomIn).Single().Values);
        Assert.Equal("AS530_CRSR", gns.Sets(1, GpsKey.Cursor).Single().Name);
    }

    [Fact]
    public void Clr_is_a_press_and_a_release_and_held_clr_a_press_of_two_seconds()
    {
        var gns = new GnsInputEvents(Baron);

        Assert.Equal([[1.0, 0.0], [0.0, 0.0]], gns.Sets(1, GpsKey.Clear).Select(s => s.Values));
        Assert.Equal([[1.0, 2.0]], gns.Sets(1, GpsKey.ClearHeld).Select(s => s.Values));
    }

    [Fact]
    public void A_key_the_unit_lacks_or_a_unit_the_aircraft_lacks_does_nothing()
    {
        var gns = new GnsInputEvents(Baron);

        Assert.Empty(gns.Sets(2, GpsKey.Vnav)); // the 430 has none
        Assert.Empty(gns.Sets(3, GpsKey.Enter));
        Assert.Empty(new GnsInputEvents([("AS530_ENT", 1)]).Sets(2, GpsKey.Enter));
    }

    [Fact]
    public void An_aircraft_without_the_stock_gns_has_no_units()
    {
        var gns = new GnsInputEvents([("LIGHTING_PANEL_1", 1), ("PMS50_GTN750_ENT", 2), ("AS510_ENT", 3)]);

        Assert.Empty(gns.Units);
        Assert.Empty(GnsInputEvents.None.Units);
    }

    [Fact]
    public void Events_listed_twice_are_taken_once() =>
        Assert.Equal(["AS530"], new GnsInputEvents([("AS530_ENT", 1), ("AS530_ENT", 1)]).Units);

    [Theory]
    [InlineData(GnsInputEvents.DescriptorSize)]
    [InlineData(GnsInputEvents.DescriptorSize + 4)] // whatever the descriptor holds beyond name, hash and type
    public void Reads_a_page_of_the_listing(int stride)
    {
        (string Name, ulong Hash)[] events = [("AS530_ENT", 11_675_888_408_130_357_189), ("AS530_CLR", 42)]; // a hash above long.MaxValue
        int size = GnsInputEvents.ListHeader + events.Length * stride;
        IntPtr data = Marshal.AllocHGlobal(size);
        try
        {
            var bytes = new byte[size];
            BitConverter.GetBytes(size).CopyTo(bytes, 0);
            BitConverter.GetBytes(34).CopyTo(bytes, 8);              // SIMCONNECT_RECV_ID_ENUMERATE_INPUT_EVENTS
            BitConverter.GetBytes(107).CopyTo(bytes, 12);            // request
            BitConverter.GetBytes(events.Length).CopyTo(bytes, 16);  // descriptors in this page
            BitConverter.GetBytes(1).CopyTo(bytes, 20);              // page 1...
            BitConverter.GetBytes(3).CopyTo(bytes, 24);              // ...of 3
            for (int i = 0; i < events.Length; i++)
            {
                int at = GnsInputEvents.ListHeader + i * stride;
                Encoding.ASCII.GetBytes(events[i].Name).CopyTo(bytes, at);
                BitConverter.GetBytes(events[i].Hash).CopyTo(bytes, at + 64);
                BitConverter.GetBytes(0).CopyTo(bytes, at + 72); // a number
            }
            Marshal.Copy(bytes, 0, data, size);

            var (request, page, pages, read) = GnsInputEvents.ReadPage(data, (uint)size);

            Assert.Equal((107u, 1u, 3u), (request, page, pages));
            Assert.Equal(events, read);
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    [Fact]
    public void The_gps_values_become_gps_1() =>
        Assert.Equal(new GpsNav(12.5, 271, true), MsfsBackend.ToGps([12.5, 271, 1]));
}
