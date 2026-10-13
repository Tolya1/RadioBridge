using RadioBridge.Core;
using RadioBridge.Core.Panel;
using RadioBridge.Core.Radio;
using RadioBridge.Core.Settings;
using RadioBridge.Core.Sim;
using static RadioBridge.Core.Panel.PanelRow;

namespace RadioBridge.FunctionalTests;

/// <summary>
/// End-to-end scenarios against the running app: panel input goes in through the automation API,
/// and each test checks both the panel displays and what the simulator itself now holds. Expected
/// values are computed from the state before the action, so the same tests run against the emulator,
/// a live X-Plane or a live MSFS. Tests that need to set up the simulator directly are skipped on MSFS.
/// </summary>
[Collection("app")]
public sealed class RadioScenarios(AppFixture fx) : IAsyncLifetime
{
    private const string Radios = "sim/cockpit2/radios/actuators/";

    private AppClient App => fx.App;

    public Task InitializeAsync() => fx.ResetSettingsAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Com(int khz) => DisplayFormat.Com(khz, compact: true);

    /// <summary>
    /// Waits until the simulator itself holds a value, read from X-Plane directly (the app shows what it set
    /// before the sim answers, so its own view isn't proof). With MSFS there is no direct access: the app's view
    /// the caller already checked has to do.
    /// </summary>
    private async Task SimHoldsAsync(string dataref, double expected, string what)
    {
        if (fx.XPlane is null)
            return;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        double actual;
        while (Math.Abs((actual = await fx.XPlane.GetAsync(dataref)) - expected) > 0.001)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for {what} in the simulator: {dataref} is {actual}, not {expected}.");
            await Task.Delay(50);
        }
    }

    /// <summary>Sets X-Plane datarefs (when the sim is reachable directly) and waits until the app sees them.</summary>
    private async Task<RadioState> GivenAsync(Func<RadioState, bool> arrived, params (string Dataref, double Value)[] values)
    {
        if (fx.XPlane is not null)
        {
            foreach (var (dataref, value) in values)
                await fx.XPlane.SetAsync(dataref, value);
            return (await App.WaitForAsync(s => arrived(s.Sim.State), "the simulator set-up to reach the app")).Sim.State;
        }
        return (await App.StateAsync()).Sim.State;
    }

    [Fact]
    public async Task Shows_the_live_radios_and_sends_the_matching_display_report()
    {
        await App.SelectAsync(Upper, Selector.Com1);
        await App.SelectAsync(Lower, Selector.Nav1);

        var state = await App.WaitForAsync(s => s.Panel is { Upper: Selector.Com1, Lower: Selector.Nav1 }, "both selectors");
        var r = state.Sim.State;
        Assert.Equal(fx.ExpectedSimName, state.Sim.Name);
        Assert.Equal(new PanelDisplays(Com(r.Com1.ActiveKhz), Com(r.Com1.StandbyKhz), DisplayFormat.Nav(r.Nav1.ActiveKhz), DisplayFormat.Nav(r.Nav1.StandbyKhz)), state.Displays);
        Assert.Equal(Convert.ToHexString(RadioPanelProtocol.EncodeDisplays(state.Displays)), state.DisplayReport);
    }

    [Fact]
    public async Task Inner_knob_tunes_com1_standby_through_833_channels()
    {
        await App.SelectAsync(Upper, Selector.Com1);
        var before = await GivenAsync(s => s.Com1.StandbyKhz == 121_500, ($"{Radios}com1_standby_frequency_hz_833", 121_500));

        await App.TurnAsync(Upper, Knob.Inner, +1, detents: 4);

        int expected = Frequencies.StepComChannel(before.Com1.StandbyKhz, 4, ComSpacing.Khz833); // 121.525: no 121.520 channel
        var state = await App.WaitForAsync(s => s.Sim.State.Com1.StandbyKhz == expected && s.Displays.UpperRight == Com(expected), $"COM1 standby {expected}");
        Assert.Equal(before.Com1.ActiveKhz, state.Sim.State.Com1.ActiveKhz);
    }

    [Fact]
    public async Task Outer_knob_tunes_whole_mhz_and_wraps_at_the_band_edge()
    {
        await App.SelectAsync(Upper, Selector.Com2);
        var before = await GivenAsync(s => s.Com2.StandbyKhz == 136_250, ($"{Radios}com2_standby_frequency_hz_833", 136_250));

        await App.TurnAsync(Upper, Knob.Outer, +1);

        int expected = Frequencies.StepComMhz(before.Com2.StandbyKhz, 1); // 118.250
        await App.WaitForAsync(s => s.Sim.State.Com2.StandbyKhz == expected && s.Displays.UpperRight == Com(expected), $"COM2 standby {expected}");
    }

    [Fact]
    public async Task Act_stby_swaps_active_and_standby()
    {
        await App.SelectAsync(Upper, Selector.Com1);
        var before = await GivenAsync(s => s.Com1 == new RadioPair(118_000, 121_500),
            ($"{Radios}com1_frequency_hz_833", 118_000), ($"{Radios}com1_standby_frequency_hz_833", 121_500));

        await App.PressAsync(Upper);

        var swapped = new RadioPair(before.Com1.StandbyKhz, before.Com1.ActiveKhz);
        await App.WaitForAsync(s => s.Sim.State.Com1 == swapped && s.Displays.UpperLeft == Com(swapped.ActiveKhz), "COM1 to swap");
    }

    [SkippableFact]
    public async Task Act_stby_swaps_radios_that_flip_when_the_press_ends()
    {
        Skip.If(fx.Emulator is null, "Emulator only: needs a radio known to swap on release (like REP's KX165 in the SF-260).");
        await App.SelectAsync(Upper, Selector.Com2);
        var before = await GivenAsync(s => s.Com2 == new RadioPair(122_800, 119_250),
            ($"{Radios}com2_frequency_hz_833", 122_800), ($"{Radios}com2_standby_frequency_hz_833", 119_250));
        fx.Emulator!.FlipOnRelease = true;
        try
        {
            await App.PressAsync(Upper);

            var swapped = new RadioPair(before.Com2.StandbyKhz, before.Com2.ActiveKhz);
            await App.WaitForAsync(s => s.Sim.State.Com2 == swapped && s.Displays.UpperLeft == Com(swapped.ActiveKhz), "COM2 to swap on release");
        }
        finally
        {
            fx.Emulator.FlipOnRelease = false;
        }
    }

    [SkippableFact]
    public async Task A_late_xplane_keeps_the_panel_on_what_was_set_and_says_so()
    {
        // GitHub issue #1: an X-Plane answering 30 s late made the panel spring back, then replay every detent.
        Skip.If(fx.Emulator is null, "Emulator only: needs an X-Plane that answers late.");
        await App.SelectAsync(Upper, Selector.Com1);
        await GivenAsync(s => s.Com1.StandbyKhz == 121_500, ($"{Radios}com1_standby_frequency_hz_833", 121_500));
        int expected = Frequencies.StepComChannel(121_500, 3, ComSpacing.Khz833);
        fx.Emulator!.RequestDelay = TimeSpan.FromSeconds(3);
        try
        {
            await App.TurnAsync(Upper, Knob.Inner, +1, detents: 3);
            await Task.Delay(1500); // well past the panel's own 800 ms hold-off

            Assert.Equal(Com(expected), (await App.StateAsync()).Displays.UpperRight);          // no spring-back
            Assert.Equal(121_500, await fx.XPlane!.GetAsync($"{Radios}com1_standby_frequency_hz_833")); // X-Plane is still behind
            await App.WaitForAsync(s => s.Sim.Notice?.Text.Contains("answering slowly") == true, "the notice that X-Plane is slow");

            await SimHoldsAsync($"{Radios}com1_standby_frequency_hz_833", expected, "X-Plane to catch up");
            Assert.Equal(Com(expected), (await App.StateAsync()).Displays.UpperRight);
        }
        finally
        {
            fx.Emulator.RequestDelay = TimeSpan.Zero;
        }

        await App.TurnAsync(Upper, Knob.Inner, -1); // answered in time again
        await App.WaitForAsync(s => s.Sim.Notice is null, "the notice to go", timeoutMs: 8000);
    }

    [Fact]
    public async Task Fast_spin_accelerates_and_panel_ends_in_agreement_with_the_sim()
    {
        // The detents arrive over HTTP, far slower than a hand spinning the knob, so widen the thresholds:
        // otherwise whether this counts as a fast spin would depend on how fast the machine is.
        await App.PutSettingsAsync(fx.InitialSettings with { ComAcceleration = new AccelerationSettings { FastDetentMs = 1000, MediumDetentMs = 2000 } });
        await App.SelectAsync(Upper, Selector.Com1);
        var before = await GivenAsync(s => s.Com1.StandbyKhz == 121_500, ($"{Radios}com1_standby_frequency_hz_833", 121_500));

        await App.TurnAsync(Upper, Knob.Inner, +1, detents: 12, intervalMs: 0);
        await Task.Delay(1500); // let the hold-off end so the display shows only what the sim reports

        var state = await App.StateAsync();
        int slow = Frequencies.StepComChannel(before.Com1.StandbyKhz, 12, ComSpacing.Khz833);
        Assert.NotEqual(slow, state.Sim.State.Com1.StandbyKhz);                    // accelerated...
        Assert.Equal(Com(state.Sim.State.Com1.StandbyKhz), state.Displays.UpperRight); // ...and nothing lost between panel and sim
    }

    [SkippableFact]
    public async Task A_change_made_in_the_cockpit_shows_on_the_panel()
    {
        Skip.If(fx.XPlane is null, "Needs direct access to the simulator (not available for MSFS).");
        await App.SelectAsync(Upper, Selector.Com1);

        await fx.XPlane!.SetAsync($"{Radios}com1_standby_frequency_hz_833", 124_350);

        await App.WaitForAsync(s => s.Displays.UpperRight == "24.350", "the cockpit change on the panel", timeoutMs: 2000);
    }

    [SkippableFact]
    public async Task A_25_khz_radio_snapping_back_is_shown_truthfully_and_long_press_fixes_the_step()
    {
        Skip.If(fx.Emulator is null, "Emulator only: needs a radio that is known not to support 8.33 kHz.");
        await App.SelectAsync(Upper, Selector.Com1);
        await GivenAsync(s => s.Com1.StandbyKhz == 121_500, ($"{Radios}com1_standby_frequency_hz_833", 121_500));
        fx.Emulator!.Com833Capable = false;
        try
        {
            await App.TurnAsync(Upper, Knob.Inner, +1); // 121.505 is snapped back to 121.500 by the radio
            await App.WaitForAsync(s => s.Displays.UpperRight == "21.500" && s.Sim.State.Com1.StandbyKhz == 121_500, "the snapped value", timeoutMs: 2500);

            await App.PressAsync(Upper, holdMs: 800); // switch the inner knob to 25 kHz
            await App.TurnAsync(Upper, Knob.Inner, +1);
            await App.WaitForAsync(s => s.Sim.State.Com1.StandbyKhz == 121_525, "a 25 kHz step");
        }
        finally
        {
            fx.Emulator.Com833Capable = true;
        }
    }

    [Fact]
    public async Task Long_press_on_com_toggles_the_spacing_and_flashes_it()
    {
        await App.SelectAsync(Upper, Selector.Com1);

        await App.PressAsync(Upper, holdMs: 800);
        var state = await App.WaitForAsync(s => s.Settings.ComSpacing == ComSpacing.Khz25, "25 kHz spacing");
        Assert.Equal("25", state.Displays.UpperLeft);

        await App.PressAsync(Upper, holdMs: 800);
        state = await App.WaitForAsync(s => s.Settings.ComSpacing == ComSpacing.Khz833, "8.33 kHz spacing");
        Assert.Equal("8.33", state.Displays.UpperLeft);
    }

    [Fact]
    public async Task Nav2_tunes_wraps_and_swaps_on_the_lower_row()
    {
        await App.SelectAsync(Lower, Selector.Nav2);
        var before = await GivenAsync(s => s.Nav2 == new RadioPair(113_900, 108_000),
            ($"{Radios}nav2_frequency_hz", 11_390), ($"{Radios}nav2_standby_frequency_hz", 10_800));

        await App.TurnAsync(Lower, Knob.Inner, -1); // 108.95: wraps inside the MHz
        await App.TurnAsync(Lower, Knob.Outer, -1); // 117.95: wraps at the band edge
        int expected = Frequencies.StepNavMhz(Frequencies.StepNavChannel(before.Nav2.StandbyKhz, -1), -1);
        await App.WaitForAsync(s => s.Sim.State.Nav2.StandbyKhz == expected, $"NAV2 standby {expected}");

        await App.PressAsync(Lower);
        await App.WaitForAsync(s => s.Sim.State.Nav2.ActiveKhz == expected && s.Displays.LowerLeft == DisplayFormat.Nav(expected), "NAV2 to swap");
    }

    [Fact]
    public async Task Adf_row_tunes_its_adf()
    {
        await App.SelectAsync(Upper, Selector.Adf);
        var before = await GivenAsync(s => s.Adf1Khz == 362, ($"{Radios}adf1_frequency_hz", 362));

        await App.TurnAsync(Upper, Knob.Outer, +1);
        await App.TurnAsync(Upper, Knob.Inner, -1, detents: 2);

        int expected = before.Adf1Khz + 8;
        await App.WaitForAsync(s => s.Sim.State.Adf1Khz == expected && s.Displays.UpperLeft == expected.ToString(), $"ADF1 {expected}");
    }

    [SkippableFact]
    public async Task Dme_shows_both_distances()
    {
        Skip.If(fx.Emulator is null, "Needs control of the DME distances (emulator only).");
        await App.SelectAsync(Lower, Selector.Dme);

        await GivenAsync(s => Math.Abs(s.Dme2Nm - 4.56) < 0.01,
            ("sim/cockpit2/radios/indicators/nav1_dme_distance_nm", 12.3), ("sim/cockpit2/radios/indicators/nav2_dme_distance_nm", 4.56));

        await App.WaitForAsync(s => s.Displays.LowerLeft == "12.3" && s.Displays.LowerRight == "4.6", "the DME distances");
    }

    /// <summary>Waits for the GNS clicks the emulator counts (a real X-Plane can't tell which ones arrived).</summary>
    private async Task<List<string>> GnsPressesAsync(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (fx.Emulator!.GnsPresses.Count < count && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        await Task.Delay(300); // nothing more arrives
        return fx.Emulator.GnsPresses.Select(p => p.Command).ToList();
    }

    [SkippableFact]
    public async Task With_gps_control_on_adf_and_dme_type_a_direct_to_into_the_gns()
    {
        Skip.If(fx.Emulator is null, "Emulator only: it counts the GNS clicks it gets.");
        await App.PutSettingsAsync(fx.InitialSettings with { GpsControl = true, LowerRowGps = 1 }); // one GNS: both rows on it
        await App.SelectAsync(Upper, Selector.Adf);
        await App.SelectAsync(Lower, Selector.Dme);
        await GivenAsync(s => s.Gps1.HasWaypoint,
            ("sim/cockpit2/radios/indicators/gps_dme_distance_nm", 23.4), ("sim/cockpit/radios/gps_obs_degm", 273), ("sim/cockpit/radios/gps_fromto", 1));
        await App.WaitForAsync(s => s.Displays == new PanelDisplays("23.4", "273", "0", "23.4"), "distance and track on ADF, D→ chosen on DME");
        fx.Emulator!.ClearGnsPresses();

        await App.PressAsync(Lower);                          // D→
        await App.TurnAsync(Upper, Knob.Inner, +1, detents: 3); // first letter
        await App.TurnAsync(Upper, Knob.Outer, +1);             // next letter
        await App.TurnAsync(Upper, Knob.Inner, -1, detents: 2);
        await App.PressAsync(Upper);                          // ENT: the waypoint
        await App.PressAsync(Upper);                          // ENT: activate

        Assert.Equal<string>(
            ["g430n1_direct", "g430n1_page_up", "g430n1_page_up", "g430n1_page_up", "g430n1_chapter_up", "g430n1_page_dn", "g430n1_page_dn", "g430n1_ent", "g430n1_ent"],
            await GnsPressesAsync(9));

        fx.Emulator.ClearGnsPresses();
        await App.TurnAsync(Lower, Knob.Inner, +1, detents: 2);
        await App.WaitForAsync(s => s.Displays.LowerLeft == "2" && s.Panel.LowerGps == new GpsRow(1, "FPL"), "FPL chosen on DME");
        await App.PressAsync(Lower);                   // FPL
        await App.PressAsync(Upper, holdMs: 800);      // CRSR
        await App.PressAsync(Lower, holdMs: 800);      // CLR

        Assert.Equal<string>(["g430n1_fpl", "g430n1_cursor", "g430n1_clr"], await GnsPressesAsync(3));
        Assert.Equal(0, fx.Emulator.IgnoredGnsPresses);
    }

    [SkippableFact]
    public async Task With_gps_control_on_the_lower_row_drives_gps_2()
    {
        Skip.If(fx.Emulator is null, "Emulator only: it counts the GNS clicks it gets.");
        await App.PutSettingsAsync(fx.InitialSettings with { GpsControl = true });
        await App.SelectAsync(Lower, Selector.Adf);
        await GivenAsync(s => !s.Gps2.HasWaypoint, ("sim/cockpit/radios/gps2_fromto", 0));
        await App.WaitForAsync(s => s.Displays.LowerLeft == "----" && s.Panel.LowerGps == new GpsRow(2, null), "GPS 2 without a waypoint");
        fx.Emulator!.ClearGnsPresses();

        await App.TurnAsync(Lower, Knob.Inner, +1);
        await App.SelectAsync(Lower, Selector.Dme);
        await App.PressAsync(Lower);

        Assert.Equal<string>(["g430n2_page_up", "g430n2_direct"], await GnsPressesAsync(2));
    }

    [Fact]
    public async Task Transponder_knobs_edit_digit_pairs()
    {
        await App.SelectAsync(Upper, Selector.Xpdr);
        var before = await GivenAsync(s => s.Squawk == 7000, ($"{Radios}transponder_code", 7000));
        await App.WaitForAsync(s => s.Displays.UpperLeft == $"{before.Squawk:0000}.", "the squawk with edit focus");

        await App.TurnAsync(Upper, Knob.Outer, +1);
        await App.TurnAsync(Upper, Knob.Inner, -1);

        int expected = Squawk.StepLowPair(Squawk.StepHighPair(before.Squawk, 1), -1); // 7000 -> 7100 -> 7177
        await App.WaitForAsync(s => s.Sim.State.Squawk == expected, $"squawk {expected:0000}");
    }

    [Fact]
    public async Task Altimeter_is_edited_after_a_press_and_reset_to_standard_with_a_hold()
    {
        await App.SelectAsync(Upper, Selector.Xpdr);
        var before = await GivenAsync(s => Math.Abs(s.BaroHpa - 1013.2) < 0.1, ("sim/cockpit2/gauges/actuators/barometer_setting_in_hg_pilot", 29.92));

        await App.PressAsync(Upper);
        await App.WaitForAsync(s => s.Displays.UpperRight == $"{Math.Round(before.BaroHpa)}.", "the altimeter to take the edit focus");
        await App.TurnAsync(Upper, Knob.Inner, +1, detents: 2);
        double expected = Math.Round(before.BaroHpa) + 2;
        await App.WaitForAsync(s => Math.Abs(s.Sim.State.BaroHpa - expected) < 0.2, $"altimeter {expected} hPa");

        await App.PressAsync(Upper, holdMs: 800);
        await App.WaitForAsync(s => Math.Abs(s.Sim.State.BaroHpa - 1013.25) < 0.2 && s.Displays.UpperRight == "1013.", "standard pressure, still editing the altimeter");

        await App.PutSettingsAsync(fx.InitialSettings with { BaroUnit = BaroUnit.InchesOfMercury });
        await App.WaitForAsync(s => s.Displays.UpperRight == "29.92.", "the altimeter in inHg");
    }

    [Theory]
    [InlineData(7000)]
    [InlineData(1200)]
    public async Task Hold_on_the_squawk_sets_the_vfr_code(int vfr)
    {
        await App.PutSettingsAsync(fx.InitialSettings with { VfrSquawk = vfr });
        await App.SelectAsync(Upper, Selector.Xpdr);
        var before = await GivenAsync(s => s.Squawk == 4521, ($"{Radios}transponder_code", 4521));
        await App.WaitForAsync(s => s.Displays.UpperLeft == $"{before.Squawk:0000}.", "the squawk with edit focus");

        await App.PressAsync(Upper, holdMs: 800);

        var state = await App.WaitForAsync(s => s.Sim.State.Squawk == vfr, $"squawk {vfr:0000} in the simulator");
        Assert.Equal($"{vfr:0000}.", state.Displays.UpperLeft); // the knobs still edit the squawk
    }

    [Fact]
    public async Task Classic_com_display_applies_immediately()
    {
        await App.SelectAsync(Upper, Selector.Com1);
        var state = await App.StateAsync();

        await App.PutSettingsAsync(fx.InitialSettings with { CompactComDisplay = false });

        string classic = DisplayFormat.Com(state.Sim.State.Com1.ActiveKhz, compact: false);
        await App.WaitForAsync(s => s.Displays.UpperLeft == classic, $"classic display {classic}");
    }

    [Fact]
    public async Task Knob_and_button_activity_is_reported_for_the_ui()
    {
        await App.SelectAsync(Lower, Selector.Dme); // read-only mode: the knobs and button change nothing in the sim
        var before = (await App.StateAsync()).Panel;

        await App.TurnAsync(Lower, Knob.Outer, -1, detents: 2);
        await App.TurnAsync(Lower, Knob.Inner, +1);
        await App.ButtonAsync(Lower, down: true);

        var state = await App.WaitForAsync(s => s.Panel.LowerActivity.ButtonDown, "ACT/STBY held");
        Assert.Equal(before.LowerActivity.OuterTurns - 2, state.Panel.LowerActivity.OuterTurns);
        Assert.Equal(before.LowerActivity.InnerTurns + 1, state.Panel.LowerActivity.InnerTurns);
        await App.WaitForAsync(s => s.Panel.LowerActivity.LongPress, "the long press to register");

        await App.ButtonAsync(Lower, down: false);
        await App.WaitForAsync(s => s.Panel.LowerActivity is { ButtonDown: false, LongPress: false }, "ACT/STBY released");
    }

    [SkippableFact]
    public async Task Test_mode_drives_the_panel_without_a_simulator_and_steps_aside_for_one()
    {
        Skip.If(fx.Emulator is null, "Emulator only: needs the simulator to go away.");
        await App.SelectAsync(Upper, Selector.Com1);
        await fx.Emulator!.StopAsync();
        try
        {
            await App.WaitForAsync(s => !s.Sim.Connected, "the simulator to go away");

            await App.PutSettingsAsync(fx.InitialSettings with { TestMode = true });
            var state = await App.WaitForAsync(s => s.Sim is { Connected: true, Name: "Test mode" } && s.Displays.UpperLeft.Length > 0, "test mode");
            int expected = Frequencies.StepComChannel(state.Sim.State.Com1.StandbyKhz, 1, ComSpacing.Khz833);

            await App.TurnAsync(Upper, Knob.Inner, +1);
            await App.WaitForAsync(s => s.Sim.State.Com1.StandbyKhz == expected && s.Displays.UpperRight == Com(expected), "test mode to tune");
        }
        finally
        {
            await fx.Emulator.StartAsync();
        }

        await App.WaitForAsync(s => s.Sim.Name == "X-Plane", "the real simulator to take over", timeoutMs: 15_000);
    }

    [SkippableFact]
    public async Task Explains_why_xplane_cannot_connect_and_recovers_when_it_is_fixed()
    {
        Skip.If(fx.Emulator is null, "Emulator only: needs an X-Plane that can be made to refuse connections.");

        await fx.Emulator!.StopAsync();
        fx.Emulator.RefuseConnections = true;
        try
        {
            await fx.Emulator.StartAsync();

            var state = await App.WaitForAsync(s => s.Sim.Notice is not null, "the app to explain the refusal", timeoutMs: 15_000);

            Assert.Contains("Disable incoming traffic", state.Sim.Notice!.Text);
            Assert.False(state.Sim.Connected);
        }
        finally
        {
            fx.Emulator.RefuseConnections = false;
        }

        // ...and has values again (they arrive a moment after the connection), so the next test starts from a live sim.
        await App.WaitForAsync(s => s.Sim.Connected && s.Sim.Notice is null && s.Sim.State.Squawk >= 0, "the app to connect and drop the notice", timeoutMs: 15_000);
    }

    [SkippableFact]
    public async Task Survives_the_simulator_restarting()
    {
        Skip.If(fx.Emulator is null, "Emulator only: restarts the simulator.");
        await App.SelectAsync(Upper, Selector.Com1);

        await fx.Emulator!.StopAsync();
        await App.WaitForAsync(s => !s.Sim.Connected && s.Displays == PanelDisplays.Blank, "the app to notice and blank the displays");

        await fx.Emulator.StartAsync();
        await App.WaitForAsync(s => s.Sim.Connected && s.Displays.UpperLeft.Length > 0, "the app to reconnect", timeoutMs: 15_000);
    }
}
