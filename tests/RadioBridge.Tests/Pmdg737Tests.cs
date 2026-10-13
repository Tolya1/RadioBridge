using Microsoft.Extensions.Time.Testing;
using RadioBridge.Core;
using RadioBridge.Core.Panel;
using RadioBridge.Core.Sim;
using RadioBridge.Core.Sim.Msfs;
using static RadioBridge.Core.Sim.Msfs.Pmdg737;

namespace RadioBridge.Tests;

/// <summary>The PMDG 737's radios, transponder and altimeter, worked through PMDG's own events (PMDG_NG3_SDK.h).</summary>
public sealed class Pmdg737Tests
{
    private static readonly RadioState Cockpit = new() { Squawk = 7000, BaroHpa = 1020 };

    private static IReadOnlyList<(uint, uint)> Events(SimCommand command, RadioState? state = null) => Translate(command, state ?? Cockpit);

    [Theory]
    [InlineData(@"SimObjects\Airplanes\PMDG 737-800\presets\pmdg\PMDG 737-800 BW TC\config\aircraft.CFG", true)] // as MSFS 2024 reported it
    [InlineData("737-800 PAX BW TC", false)] // that aircraft's title: not enough on its own
    [InlineData("PMDG 737-800 Boeing House", true)]
    [InlineData("pmdg 737-700BW Southwest", true)]
    [InlineData("PMDG 777-300ER", false)]
    [InlineData("Boeing 737 MAX 8", false)]
    [InlineData(null, false)]
    public void Knows_the_737_by_its_title(string? title, bool pmdg) => Assert.Equal(pmdg, IsPmdg737(title));

    [Fact]
    public void Its_event_numbers_count_from_69632() => Assert.Equal(69_632u, EventBase);

    [Fact]
    public void Radio_knobs_turn_a_wheel_step_per_detent()
    {
        Assert.Equal([(Com1Inner, MouseWheelUp), (Com1Inner, MouseWheelUp)], Events(new TurnKnobs(KnobTarget.Com1, false, 2, 121_500, 121_510)));
        Assert.Equal([(Com2Outer, MouseWheelDown)], Events(new TurnKnobs(KnobTarget.Com2, true, -1, 122_800, 121_800)));
        Assert.Equal([(Nav1Outer, MouseWheelUp)], Events(new TurnKnobs(KnobTarget.Nav1, true, 1, 110_300, 111_300)));
        Assert.Equal([(Nav2Inner, MouseWheelDown)], Events(new TurnKnobs(KnobTarget.Nav2, false, -1, 113_900, 113_850)));
    }

    [Fact]
    public void Act_stby_clicks_the_transfer_switch()
    {
        Assert.Equal([(Com1Transfer, MouseLeftSingle), (Com1Transfer, MouseLeftRelease)], Events(new SwapRadio(Tunable.Com1)));
        Assert.Equal(Nav2Transfer, Events(new SwapRadio(Tunable.Nav2))[0].Item1);
    }

    [Fact]
    public void The_baro_knob_turns_once_per_inner_detent_and_ten_times_per_outer()
    {
        Assert.Equal([(CaptainBaro, MouseWheelDown)], Events(new TurnKnobs(KnobTarget.Baro, false, -1, 1020, 1019)));
        Assert.Equal(Enumerable.Repeat((CaptainBaro, MouseWheelUp), 10), Events(new TurnKnobs(KnobTarget.Baro, true, 1, 1020, 1030)));
    }

    [Fact]
    public void Standard_pressure_presses_std_unless_it_is_on_already()
    {
        Assert.Equal([(CaptainBaroStd, MouseLeftSingle), (CaptainBaroStd, MouseLeftRelease)], Events(new SetBaroStandard()));
        Assert.Empty(Events(new SetBaroStandard(), Cockpit with { BaroHpa = 1013.25 })); // pressing again would take it off
    }

    [Theory]
    [InlineData(7000, 1200, new[] { XpdrKnob1, XpdrKnob1, XpdrKnob2, XpdrKnob2 }, true)] // 7 -> 1 and 0 -> 2: two up each
    [InlineData(7000, 7700, new[] { XpdrKnob2 }, false)]                                   // 0 -> 7: one down, the short way
    [InlineData(2000, 2000, new uint[0], true)]
    public void The_transponder_turns_each_digit_knob_the_short_way(int from, int to, uint[] knobs, bool up)
    {
        var events = Events(new TurnKnobs(KnobTarget.Squawk, false, 0, from, to));

        Assert.Equal(knobs, events.Select(e => e.Item1));
        Assert.All(events, e => Assert.Equal(up ? MouseWheelUp : MouseWheelDown, e.Item2));
    }

    [Fact]
    public void The_adf_is_left_alone() =>
        Assert.Empty(Events(new TurnKnobs(KnobTarget.Adf1, false, 1, 362, 363)));
}

/// <summary>The panel sending knob turns, not values, while the aircraft only takes turns.</summary>
public sealed class KnobTurnTests : IDisposable
{
    private static readonly RadioState Cockpit = new()
    {
        Com1 = new RadioPair(118_000, 121_500),
        Squawk = 7000,
        BaroHpa = 1013.25,
    };

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-13T18:00:00Z"));
    private readonly FakePanel _panel = new();
    private readonly FakeSim _sim = new("MSFS") { TurnsKnobs = true };
    private readonly TempSettings _settings = new();
    private readonly PanelController _controller;

    public KnobTurnTests()
    {
        _controller = new PanelController(_panel, _sim, _settings.Store, _time);
        _controller.Start();
        _sim.Connect(Cockpit);
        _panel.Raise(new SelectorMoved(PanelRow.Upper, Selector.Com1));
        _panel.Raise(new SelectorMoved(PanelRow.Lower, Selector.Xpdr));
    }

    public void Dispose()
    {
        _controller.Dispose();
        _settings.Dispose();
    }

    [Fact]
    public void A_com_detent_goes_out_as_a_knob_turn_and_shows_the_value_it_leads_to()
    {
        _panel.Raise(new KnobTurned(PanelRow.Upper, Knob.Inner, 1));

        Assert.Equal(new TurnKnobs(KnobTarget.Com1, false, 1, 121_500, 121_505), _sim.Sent.Single());
        Assert.Equal("21.505", _panel.Shown.UpperRight);
    }

    [Fact]
    public void Squawk_knobs_and_the_vfr_hold_go_out_as_turns_from_the_current_code()
    {
        _panel.Raise(new KnobTurned(PanelRow.Lower, Knob.Outer, 1));
        _time.Advance(TimeSpan.FromSeconds(2)); // the turn is forgotten; the sim still says 7000
        _panel.Raise(new ButtonChanged(PanelRow.Lower, true));
        _time.Advance(TimeSpan.FromMilliseconds(700));
        _panel.Raise(new ButtonChanged(PanelRow.Lower, false));

        Assert.Equal([new TurnKnobs(KnobTarget.Squawk, true, 1, 7000, 7100), new TurnKnobs(KnobTarget.Squawk, false, 0, 7000, 7000)], _sim.Sent);
    }

    [Fact]
    public void Swaps_and_standard_pressure_stay_as_they_are()
    {
        _panel.Raise(new ButtonChanged(PanelRow.Upper, true));
        _time.Advance(TimeSpan.FromMilliseconds(100));
        _panel.Raise(new ButtonChanged(PanelRow.Upper, false));

        Assert.Equal(new SwapRadio(Tunable.Com1), _sim.Sent.Single());
    }

    [Fact]
    public void Other_aircraft_still_get_values()
    {
        _sim.TurnsKnobs = false;

        _panel.Raise(new KnobTurned(PanelRow.Upper, Knob.Inner, 1));

        Assert.Equal(new SetStandby(Tunable.Com1, 121_505), _sim.Sent.Single());
    }
}
