using Microsoft.Extensions.Time.Testing;
using RadioBridge.Core;
using RadioBridge.Core.Panel;
using RadioBridge.Core.Radio;
using RadioBridge.Core.Sim;

namespace RadioBridge.Tests;

public sealed class PanelControllerTests : IDisposable
{
    private static readonly RadioState Cockpit = new()
    {
        Com1 = new RadioPair(118_000, 121_500),
        Com2 = new RadioPair(122_800, 119_250),
        Nav1 = new RadioPair(110_300, 113_900),
        Adf1Khz = 362,
        Squawk = 7000,
        BaroHpa = 1013.25,
    };

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-10T12:00:00Z"));
    private readonly FakePanel _panel = new();
    private readonly FakeSim _sim = new("X-Plane");
    private readonly TempSettings _settings = new();
    private readonly PanelController _controller;

    public PanelControllerTests()
    {
        _controller = new PanelController(_panel, _sim, _settings.Store, _time);
        _controller.Start();
        _sim.Connect(Cockpit);
        _panel.Raise(new SelectorMoved(PanelRow.Upper, Selector.Com1));
        _panel.Raise(new SelectorMoved(PanelRow.Lower, Selector.Nav1));
    }

    public void Dispose()
    {
        _controller.Dispose();
        _settings.Dispose();
    }

    private void Turn(PanelRow row, Knob knob, int direction, int times = 1)
    {
        for (int i = 0; i < times; i++)
        {
            _panel.Raise(new KnobTurned(row, knob, direction));
            _time.Advance(TimeSpan.FromMilliseconds(500)); // slow: no acceleration
        }
    }

    private void Press(PanelRow row, TimeSpan hold)
    {
        _panel.Raise(new ButtonChanged(row, true));
        _time.Advance(hold);
        _panel.Raise(new ButtonChanged(row, false));
    }

    [Fact]
    public void Shows_com_compact_and_nav_classic()
    {
        Assert.Equal(new PanelDisplays("18.000", "21.500", "110.30", "113.90"), _panel.Shown);
        Assert.Equal("X-Plane", _controller.View.SimName);
    }

    [Fact]
    public void Inner_knob_tunes_833_channels_on_standby()
    {
        _panel.Raise(new KnobTurned(PanelRow.Upper, Knob.Inner, 1));

        Assert.Equal(new SetStandby(Tunable.Com1, 121_505), _sim.Sent.Single());
        Assert.Equal("21.505", _panel.Shown.UpperRight);
    }

    [Fact]
    public void Consecutive_detents_build_on_the_panel_value_not_the_lagging_sim_value()
    {
        Turn(PanelRow.Upper, Knob.Inner, 1, times: 1);
        _panel.Raise(new KnobTurned(PanelRow.Upper, Knob.Inner, 1));

        Assert.Equal(new SetStandby(Tunable.Com1, 121_510), _sim.Sent[^1]);
    }

    [Fact]
    public void Stale_sim_value_is_ignored_during_hold_off_then_sim_wins()
    {
        _panel.Raise(new KnobTurned(PanelRow.Upper, Knob.Inner, 1));
        _sim.Report(Cockpit); // still the old 121.500

        Assert.Equal("21.505", _panel.Shown.UpperRight);

        _time.Advance(TimeSpan.FromSeconds(1)); // hold-off over: the radio evidently snapped back
        Assert.Equal("21.500", _panel.Shown.UpperRight);
    }

    [Fact]
    public void Confirmed_value_hands_control_back_to_the_sim_immediately()
    {
        _panel.Raise(new KnobTurned(PanelRow.Upper, Knob.Inner, 1));
        _sim.Report(Cockpit with { Com1 = new RadioPair(118_000, 121_505) });
        _sim.Report(Cockpit with { Com1 = new RadioPair(118_000, 124_350) }); // tuned in the cockpit

        Assert.Equal("24.350", _panel.Shown.UpperRight);
    }

    [Fact]
    public void Fast_spin_accelerates_on_com()
    {
        _panel.Raise(new KnobTurned(PanelRow.Upper, Knob.Inner, 1));
        _time.Advance(TimeSpan.FromMilliseconds(10));
        _panel.Raise(new KnobTurned(PanelRow.Upper, Knob.Inner, 1));

        // 121.505 + 3 channels (default fast multiplier) = 121.525 (121.520 isn't a channel)
        Assert.Equal(new SetStandby(Tunable.Com1, 121_525), _sim.Sent[^1]);
    }

    [Fact]
    public void Medium_spin_doubles_the_com_step()
    {
        _panel.Raise(new KnobTurned(PanelRow.Upper, Knob.Outer, 1));
        _time.Advance(TimeSpan.FromMilliseconds(30));
        _panel.Raise(new KnobTurned(PanelRow.Upper, Knob.Outer, 1));

        Assert.Equal(new SetStandby(Tunable.Com1, 124_500), _sim.Sent[^1]); // 121.5 -> 122.5 -> 124.5
    }

    [Fact]
    public void Normal_turning_is_never_accelerated()
    {
        _panel.Raise(new KnobTurned(PanelRow.Upper, Knob.Inner, 1));
        _time.Advance(TimeSpan.FromMilliseconds(60));
        _panel.Raise(new KnobTurned(PanelRow.Upper, Knob.Inner, 1));

        Assert.Equal(new SetStandby(Tunable.Com1, 121_510), _sim.Sent[^1]);
    }

    [Fact]
    public void Fast_spins_outside_com_move_one_step_per_detent()
    {
        // NAV1 on the lower row: 113.90 -> 113.95 -> wraps to 113.00, one 50 kHz step each.
        _panel.Raise(new KnobTurned(PanelRow.Lower, Knob.Inner, 1));
        _time.Advance(TimeSpan.FromMilliseconds(10));
        _panel.Raise(new KnobTurned(PanelRow.Lower, Knob.Inner, 1));
        Assert.Equal(new SetStandby(Tunable.Nav1, 113_000), _sim.Sent[^1]);

        _panel.Raise(new SelectorMoved(PanelRow.Upper, Selector.Xpdr));
        _time.Advance(TimeSpan.FromSeconds(1));
        for (int i = 0; i < 3; i++)
        {
            _panel.Raise(new KnobTurned(PanelRow.Upper, Knob.Inner, 1));
            _time.Advance(TimeSpan.FromMilliseconds(10));
        }
        Assert.Equal(new SetSquawk(7003), _sim.Sent[^1]);
    }

    [Fact]
    public void Outer_knob_tunes_nav_mhz()
    {
        _panel.Raise(new KnobTurned(PanelRow.Lower, Knob.Outer, -1));

        Assert.Equal(new SetStandby(Tunable.Nav1, 112_900), _sim.Sent.Single());
    }

    [Fact]
    public void Short_press_swaps()
    {
        Press(PanelRow.Upper, TimeSpan.FromMilliseconds(100));

        Assert.Equal(new SwapRadio(Tunable.Com1), _sim.Sent.Single());
    }

    private (string Active, string Standby) UpperRow => (_panel.Shown.UpperLeft, _panel.Shown.UpperRight);

    [Fact]
    public void A_swap_shows_at_once_and_rides_out_a_radio_that_swaps_in_two_steps()
    {
        Press(PanelRow.Upper, TimeSpan.FromMilliseconds(100));
        Assert.Equal(("21.500", "18.000"), UpperRow); // before the sim has swapped

        _sim.Report(Cockpit with { Com1 = new RadioPair(118_000, 118_000) }); // REP's KX165 writes the standby first...
        Assert.Equal(("21.500", "18.000"), UpperRow);
        _sim.Report(Cockpit with { Com1 = new RadioPair(121_500, 118_000) }); // ...and the active a frame later
        Assert.Equal(("21.500", "18.000"), UpperRow);

        _sim.Report(Cockpit with { Com1 = new RadioPair(124_000, 118_000) }); // confirmed: the sim decides again
        Assert.Equal(("24.000", "18.000"), UpperRow);
    }

    [Fact]
    public void A_radio_that_does_not_swap_is_shown_truthfully_after_the_hold_off()
    {
        Press(PanelRow.Upper, TimeSpan.FromMilliseconds(100));
        Assert.Equal(("21.500", "18.000"), UpperRow);

        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(("18.000", "21.500"), UpperRow);
    }

    [Fact]
    public void A_standby_value_tuned_just_before_the_press_is_the_one_swapped_in()
    {
        _panel.Raise(new KnobTurned(PanelRow.Upper, Knob.Inner, 1)); // 121.505, not yet confirmed by the sim

        Press(PanelRow.Upper, TimeSpan.FromMilliseconds(100));

        Assert.Equal(("21.505", "18.000"), UpperRow);
        Assert.Equal(new SwapRadio(Tunable.Com1), _sim.Sent[^1]);
    }

    [Fact]
    public void Long_press_on_com_toggles_spacing_and_flashes_it()
    {
        Press(PanelRow.Upper, TimeSpan.FromMilliseconds(700));

        Assert.Equal(ComSpacing.Khz25, _settings.Store.Current.ComSpacing);
        Assert.Empty(_sim.Sent); // no swap on release
        Assert.Equal("25", _panel.Shown.UpperLeft);

        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("18.000", _panel.Shown.UpperLeft);
        _panel.Raise(new KnobTurned(PanelRow.Upper, Knob.Inner, 1));
        Assert.Equal(new SetStandby(Tunable.Com1, 121_525), _sim.Sent[^1]);
    }

    [Fact]
    public void Xpdr_knobs_edit_squawk_and_button_switches_to_baro()
    {
        _panel.Raise(new SelectorMoved(PanelRow.Upper, Selector.Xpdr));
        Assert.Equal(("7000.", "1013"), (_panel.Shown.UpperLeft, _panel.Shown.UpperRight));

        _panel.Raise(new KnobTurned(PanelRow.Upper, Knob.Outer, 1));
        Assert.Equal(new SetSquawk(7100), _sim.Sent[^1]);

        Press(PanelRow.Upper, TimeSpan.FromMilliseconds(100));
        Assert.Equal("1013.", _panel.Shown.UpperRight);
        _time.Advance(TimeSpan.FromSeconds(1));
        _panel.Raise(new KnobTurned(PanelRow.Upper, Knob.Inner, 1));
        Assert.Equal(new SetBaro(1014), _sim.Sent[^1]);
    }

    [Theory]
    [InlineData(7000)]
    [InlineData(1200)]
    public void Hold_on_the_squawk_sets_the_vfr_code(int vfr)
    {
        _settings.Store.Update(s => s with { VfrSquawk = vfr });
        _sim.Report(Cockpit with { Squawk = 4521 });
        _panel.Raise(new SelectorMoved(PanelRow.Lower, Selector.Xpdr));

        Press(PanelRow.Lower, TimeSpan.FromMilliseconds(700));

        Assert.Equal(new SetSquawk(vfr), _sim.Sent.Single());
        Assert.Equal(($"{vfr:0000}.", "1013"), (_panel.Shown.LowerLeft, _panel.Shown.LowerRight)); // still editing the squawk
    }

    [Fact]
    public void Hold_on_the_altimeter_sets_standard_pressure()
    {
        _panel.Raise(new SelectorMoved(PanelRow.Upper, Selector.Xpdr));
        Press(PanelRow.Upper, TimeSpan.FromMilliseconds(100)); // select the altimeter

        Press(PanelRow.Upper, TimeSpan.FromMilliseconds(700));

        Assert.IsType<SetBaroStandard>(_sim.Sent.Single());
        Assert.Equal(("7000", "1013."), (_panel.Shown.UpperLeft, _panel.Shown.UpperRight)); // still editing the altimeter
    }

    [Fact]
    public void Presses_on_xpdr_move_the_edit_focus_back_and_forth()
    {
        _panel.Raise(new SelectorMoved(PanelRow.Upper, Selector.Xpdr));

        for (int i = 0; i < 3; i++)
        {
            Press(PanelRow.Upper, TimeSpan.FromMilliseconds(80));
            _time.Advance(TimeSpan.FromMilliseconds(100));
        }

        Assert.Empty(_sim.Sent);
        Assert.Equal(("7000", "1013."), (_panel.Shown.UpperLeft, _panel.Shown.UpperRight)); // three toggles: altimeter
    }

    [Fact]
    public void Adf_row_tunes_its_own_adf()
    {
        _panel.Raise(new SelectorMoved(PanelRow.Upper, Selector.Adf));
        _panel.Raise(new KnobTurned(PanelRow.Upper, Knob.Outer, 1));

        Assert.Equal(new SetAdf(1, 372), _sim.Sent.Single());
        Assert.Equal("372", _panel.Shown.UpperLeft);
    }

    [Fact]
    public void Disconnected_sim_blanks_displays_and_ignores_knobs()
    {
        _sim.Disconnect();
        _panel.Raise(new KnobTurned(PanelRow.Upper, Knob.Inner, 1));

        Assert.Equal(PanelDisplays.Blank, _panel.Shown);
        Assert.Empty(_sim.Sent);
        Assert.False(_controller.View.SimConnected);
    }
}

public class SimRouterTests
{
    [Fact]
    public void Follows_the_first_connected_sim_and_fails_over()
    {
        var msfs = new FakeSim("MSFS");
        var xplane = new FakeSim("X-Plane");
        using var router = new SimRouter([msfs, xplane]);
        int changes = 0;
        router.Changed += () => changes++;

        Assert.False(router.IsConnected);

        xplane.Connect(RadioState.Empty with { Squawk = 1200 });
        msfs.Connect(RadioState.Empty with { Squawk = 7000 });
        Assert.Equal("X-Plane", router.Name);
        Assert.Equal(1200, router.State.Squawk);

        router.Send(new SetSquawk(2000));
        Assert.Single(xplane.Sent);
        Assert.Empty(msfs.Sent);

        int before = changes;
        msfs.Report(RadioState.Empty with { Squawk = 7001 }); // inactive sim: no refresh
        Assert.Equal(before, changes);

        xplane.Disconnect();
        Assert.Equal("MSFS", router.Name);
        Assert.Equal(7001, router.State.Squawk);
    }
}
