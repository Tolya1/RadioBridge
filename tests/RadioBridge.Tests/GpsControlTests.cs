using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using RadioBridge.Core;
using RadioBridge.Core.Panel;
using RadioBridge.Core.Radio;
using RadioBridge.Core.Sim;
using RadioBridge.Core.Sim.XPlane;
using RadioBridge.FakeXPlane;
using static RadioBridge.Core.Panel.PanelRow;

namespace RadioBridge.Tests;

/// <summary>ADF and DME driving the GNS 430/530: ADF types, DME has the other keys; upper row GPS 1, lower row GPS 2.</summary>
public sealed class GpsControlTests : IDisposable
{
    private static readonly RadioState Cockpit = new()
    {
        Adf1Khz = 362,
        Adf2Khz = 1_160,
        Dme1Nm = 12.3,
        Squawk = 7000,
        Gps1 = new GpsNav(23.46, 273.4, true),
        Gps2 = GpsNav.None,
    };

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-13T12:00:00Z"));
    private readonly FakePanel _panel = new();
    private readonly FakeSim _sim = new("X-Plane") { SupportsGpsKeys = true };
    private readonly TempSettings _settings = new();
    private readonly PanelController _controller;

    public GpsControlTests()
    {
        _settings.Store.Update(s => s with { GpsControl = true });
        _controller = new PanelController(_panel, _sim, _settings.Store, _time);
        _controller.Start();
        _sim.Connect(Cockpit);
        _panel.Raise(new SelectorMoved(Upper, Selector.Adf));
        _panel.Raise(new SelectorMoved(Lower, Selector.Dme));
    }

    public void Dispose()
    {
        _controller.Dispose();
        _settings.Dispose();
    }

    private List<PressGpsKey> Keys => _sim.Sent.OfType<PressGpsKey>().ToList();

    private void Turn(PanelRow row, Knob knob, int direction, int times = 1)
    {
        for (int i = 0; i < times; i++)
        {
            _panel.Raise(new KnobTurned(row, knob, direction));
            _time.Advance(TimeSpan.FromMilliseconds(100));
        }
    }

    private void Press(PanelRow row, int holdMs = 100)
    {
        _panel.Raise(new ButtonChanged(row, true));
        _time.Advance(TimeSpan.FromMilliseconds(holdMs));
        _panel.Raise(new ButtonChanged(row, false));
    }

    [Fact]
    public void Adf_knobs_are_the_gns_right_hand_knobs()
    {
        Turn(Upper, Knob.Outer, +1);
        Turn(Upper, Knob.Inner, -1);
        Turn(Upper, Knob.Inner, +1, times: 2);

        Assert.Equal<PressGpsKey>(
            [new(1, GpsKey.LargeKnobUp), new(1, GpsKey.SmallKnobDown), new(1, GpsKey.SmallKnobUp), new(1, GpsKey.SmallKnobUp)],
            Keys);
        Assert.DoesNotContain(_sim.Sent, c => c is SetAdf);
    }

    [Fact]
    public void A_tap_on_adf_is_ent_and_a_hold_is_crsr()
    {
        Press(Upper);
        Press(Upper, holdMs: 700);

        Assert.Equal<PressGpsKey>([new(1, GpsKey.Enter), new(1, GpsKey.Cursor)], Keys); // no ENT when the hold ends
    }

    [Fact]
    public void A_tap_on_dme_is_direct_to_and_its_small_knob_chooses_the_other_keys()
    {
        Press(Lower);
        Turn(Lower, Knob.Inner, +2);
        Assert.Equal("2", _panel.Shown.LowerLeft);
        Assert.Equal(new GpsRow(2, "FPL"), _controller.View.LowerGps);
        Assert.Empty(Keys.Skip(1)); // choosing presses nothing

        Press(Lower);
        Press(Lower); // the choice went back to D→

        Assert.Equal<PressGpsKey>([new(2, GpsKey.DirectTo), new(2, GpsKey.FlightPlan), new(2, GpsKey.DirectTo)], Keys);
        Assert.Equal("0", _panel.Shown.LowerLeft);
    }

    [Fact]
    public void The_choice_stops_at_clr_held_on_the_left_and_vnav_on_the_right()
    {
        Turn(Lower, Knob.Inner, -3);
        Assert.Equal("-1", _panel.Shown.LowerLeft);
        Press(Lower);

        Turn(Lower, Knob.Inner, +20);
        Assert.Equal("7", _panel.Shown.LowerLeft);
        Press(Lower);

        Assert.Equal<PressGpsKey>([new(2, GpsKey.ClearHeld), new(2, GpsKey.Vnav)], Keys);
    }

    [Fact]
    public void A_hold_on_dme_is_clr_and_its_big_knob_sets_the_map_range()
    {
        Press(Lower, holdMs: 700);
        Turn(Lower, Knob.Outer, +1);
        Turn(Lower, Knob.Outer, -1);

        Assert.Equal<PressGpsKey>([new(2, GpsKey.Clear), new(2, GpsKey.ZoomOut), new(2, GpsKey.ZoomIn)], Keys);
    }

    [Fact]
    public void In_aircraft_with_one_gns_the_lower_row_can_drive_gps_1_too()
    {
        _settings.Store.Update(s => s with { LowerRowGps = 1 });

        Press(Lower);
        Turn(Upper, Knob.Inner, +1);

        Assert.Equal<PressGpsKey>([new(1, GpsKey.DirectTo), new(1, GpsKey.SmallKnobUp)], Keys);
        Assert.Equal(new GpsRow(1, "D→"), _controller.View.LowerGps);
    }

    [Fact]
    public void Adf_shows_distance_and_desired_track_and_dme_the_chosen_key_and_distance()
    {
        Assert.Equal(new PanelDisplays("23.5", "273", "0", DisplayFormat.NoGps), _panel.Shown); // GPS 2 has no waypoint

        _sim.Report(Cockpit with { Gps2 = new GpsNav(4.04, 90, true) });

        Assert.Equal("4.0", _panel.Shown.LowerRight);
    }

    [Fact]
    public void Moving_the_selector_starts_the_choice_at_direct_to_again()
    {
        Turn(Lower, Knob.Inner, +3);
        _panel.Raise(new SelectorMoved(Lower, Selector.Adf));
        _panel.Raise(new SelectorMoved(Lower, Selector.Dme));

        Assert.Equal("0", _panel.Shown.LowerLeft);
    }

    [Fact]
    public void The_window_is_told_which_gps_each_row_drives()
    {
        Assert.Equal(new GpsRow(1, null), _controller.View.UpperGps);
        Assert.Equal(new GpsRow(2, "D→"), _controller.View.LowerGps);
    }

    [Fact]
    public void With_gps_control_off_adf_and_dme_stay_radios()
    {
        _settings.Store.Update(s => s with { GpsControl = false });

        Turn(Upper, Knob.Outer, +1);
        Press(Lower);

        Assert.Equal(new SetAdf(1, 372), _sim.Sent.Single());
        Assert.Equal(new PanelDisplays("372", "", "12.3", ""), _panel.Shown);
        Assert.Null(_controller.View.UpperGps);
    }

    [Fact]
    public void A_simulator_that_takes_no_gps_keys_keeps_adf_and_dme_as_radios()
    {
        _sim.SupportsGpsKeys = false;
        _sim.Report(Cockpit);

        Turn(Upper, Knob.Inner, +1);

        Assert.Equal(new SetAdf(1, 363), _sim.Sent.Single());
        Assert.Equal("363", _panel.Shown.UpperLeft);
    }

    [Fact]
    public void Other_positions_are_unchanged()
    {
        _panel.Raise(new SelectorMoved(Upper, Selector.Xpdr));

        Press(Upper, holdMs: 700);

        Assert.Equal(new SetSquawk(7000), _sim.Sent.Single());
        Assert.Empty(Keys);
    }
}

public sealed class GpsDisplayFormatTests
{
    [Theory]
    [InlineData(273.4, "273")]
    [InlineData(4.6, "005")]
    [InlineData(0.2, "360")]
    [InlineData(359.7, "360")]
    [InlineData(-90, "270")]
    public void Desired_track_reads_like_a_compass(double degrees, string shown) =>
        Assert.Equal(shown, DisplayFormat.GpsTrack(new GpsNav(10, degrees, true)));

    [Theory]
    [InlineData(0.04, "0.0")]
    [InlineData(23.46, "23.5")]
    [InlineData(12_345.6, "9999.9")]
    public void Distance_has_one_decimal_and_fits_the_display(double nm, string shown) =>
        Assert.Equal(shown, DisplayFormat.GpsDistance(new GpsNav(nm, 90, true)));

    [Fact]
    public void Without_a_waypoint_both_show_dashes()
    {
        var flagged = new GpsNav(12, 90, false);

        Assert.Equal("----", DisplayFormat.GpsDistance(flagged));
        Assert.Equal("----", DisplayFormat.GpsTrack(flagged));
    }
}

/// <summary>GPS keys against the X-Plane emulator, which counts the GNS clicks it gets.</summary>
public sealed class XPlaneGpsTests : IAsyncLifetime
{
    private readonly XPlaneEmulator _xplane = new();
    private XPlaneBackend _backend = null!;

    public async Task InitializeAsync()
    {
        await _xplane.StartAsync();
        _backend = new XPlaneBackend(() => _xplane.Port);
        _backend.Start();
        await Wait.Until(() => _backend.IsConnected && _backend.State.Squawk >= 0, "the backend to connect");
    }

    public async Task DisposeAsync()
    {
        _backend.Dispose();
        await _xplane.DisposeAsync();
    }

    private async Task<List<GnsPress>> PressesAsync(int count)
    {
        await Wait.Until(() => _xplane.GnsPresses.Count >= count, $"{count} GNS presses");
        await Task.Delay(200); // nothing more arrives
        return _xplane.GnsPresses.ToList();
    }

    [Fact]
    public void Takes_gps_keys() => Assert.True(_backend.SupportsGpsKeys);

    [Fact]
    public async Task Keys_press_the_built_in_gns_commands_of_each_unit_for_as_long_as_a_finger_would()
    {
        _backend.Send(new PressGpsKey(1, GpsKey.Enter));
        _backend.Send(new PressGpsKey(2, GpsKey.FlightPlan));
        _backend.Send(new PressGpsKey(1, GpsKey.LargeKnobDown));
        _backend.Send(new PressGpsKey(2, GpsKey.ZoomOut));
        _backend.Send(new PressGpsKey(1, GpsKey.ClearHeld));

        var presses = await PressesAsync(5);

        Assert.Equal<string>(["g430n1_ent", "g430n2_fpl", "g430n1_chapter_dn", "g430n2_zoom_out", "g430n1_clr"], presses.Select(p => p.Command));
        Assert.Equal<double>([XPlaneBackend.PressSeconds, XPlaneBackend.PressSeconds, XPlaneBackend.KnobClickSeconds, XPlaneBackend.KnobClickSeconds, XPlaneBackend.ClearHeldSeconds],
            presses.Select(p => Math.Round(p.Held.TotalSeconds, 3)));
    }

    [Fact]
    public async Task Every_click_of_a_fast_spin_counts_and_keys_keep_their_order()
    {
        for (int i = 0; i < 6; i++)
            _backend.Send(new PressGpsKey(1, GpsKey.SmallKnobUp)); // all at once, faster than any hand
        _backend.Send(new PressGpsKey(1, GpsKey.LargeKnobUp));
        _backend.Send(new PressGpsKey(1, GpsKey.SmallKnobUp));
        _backend.Send(new PressGpsKey(1, GpsKey.Enter));

        var presses = await PressesAsync(9);

        Assert.Equal<string>([.. Enumerable.Repeat("g430n1_page_up", 6), "g430n1_chapter_up", "g430n1_page_up", "g430n1_ent"], presses.Select(p => p.Command));
        Assert.Equal(0, _xplane.IgnoredGnsPresses); // none started while the last one was still held
    }

    [Fact]
    public async Task Reads_where_each_gps_is_heading()
    {
        _xplane["sim/cockpit2/radios/indicators/gps_dme_distance_nm"] = 23.4;
        _xplane["sim/cockpit/radios/gps_obs_degm"] = 273;
        _xplane["sim/cockpit/radios/gps_fromto"] = 1;
        _xplane["sim/cockpit2/radios/indicators/gps2_dme_distance_nm"] = 7;

        await Wait.Until(() => _backend.State.Gps1 == new GpsNav(23.4, 273, true), "GPS 1's waypoint");
        Assert.Equal(new GpsNav(7, 0, false), _backend.State.Gps2); // flagged: no waypoint
    }
}

/// <summary>The emulator's GNS model, which the fast-spin test relies on.</summary>
public sealed class XPlaneEmulatorGnsTests : IAsyncLifetime
{
    private readonly XPlaneEmulator _xplane = new();

    public Task InitializeAsync() => _xplane.StartAsync();

    public async Task DisposeAsync() => await _xplane.DisposeAsync();

    private async Task PressAsync(System.Net.WebSockets.ClientWebSocket socket, string command, double seconds)
    {
        string json = JsonSerializer.Serialize(new
        {
            req_id = 1,
            type = "command_set_is_active",
            @params = new { commands = new[] { new { id = _xplane.IdOf(command), is_active = true, duration = seconds } } },
        });
        await socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(json), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
        await socket.ReceiveAsync(new byte[4096], CancellationToken.None); // the result
    }

    [Fact]
    public async Task A_press_that_starts_while_the_same_command_is_held_is_not_a_click_of_its_own()
    {
        using var socket = new System.Net.WebSockets.ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_xplane.Port}/api/v2"), CancellationToken.None);

        await PressAsync(socket, "sim/GPS/g430n1_page_up", 0.3);
        await PressAsync(socket, "sim/GPS/g430n1_page_up", 0.3); // still held
        await PressAsync(socket, "sim/GPS/g430n1_ent", 0.1);     // another command is fine
        await Task.Delay(400);
        await PressAsync(socket, "sim/GPS/g430n1_page_up", 0.3); // released by now

        Assert.Equal<string>(["g430n1_page_up", "g430n1_ent", "g430n1_page_up"], _xplane.GnsPresses.Select(p => p.Command));
        Assert.Equal(1, _xplane.IgnoredGnsPresses);
    }
}
