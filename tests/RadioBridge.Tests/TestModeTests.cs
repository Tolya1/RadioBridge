using Microsoft.Extensions.Time.Testing;
using RadioBridge.Core;
using RadioBridge.Core.Panel;
using RadioBridge.Core.Sim;

namespace RadioBridge.Tests;

public class TestSimBackendTests
{
    private bool _enabled = true;
    private readonly FakeTimeProvider _time = new();

    private TestSimBackend Start()
    {
        var test = new TestSimBackend(() => _enabled, _time);
        test.Start();
        return test;
    }

    [Fact]
    public void Connected_only_while_test_mode_is_on()
    {
        using var test = Start();
        Assert.True(test.IsConnected);
        Assert.Equal(TestSimBackend.InitialState, test.State);

        _enabled = false;
        test.Refresh();
        Assert.False(test.IsConnected);
        Assert.Equal(RadioState.Empty, test.State);
    }

    [Fact]
    public void Applies_commands_like_a_radio()
    {
        using var test = Start();

        test.Send(new SetStandby(Tunable.Com1, 121_505));
        test.Send(new SwapRadio(Tunable.Com1));
        test.Send(new SetStandby(Tunable.Nav1, 110_340)); // NAV snaps to 50 kHz
        test.Send(new SetSquawk(1200));
        test.Send(new SetBaro(1000));
        test.Send(new SetBaroStandard());

        var s = test.State;
        Assert.Equal(new RadioPair(121_505, 124_355), s.Com1);
        Assert.Equal(110_350, s.Nav1.StandbyKhz);
        Assert.Equal(1200, s.Squawk);
        Assert.Equal(1013.25, s.BaroHpa);
    }

    [Fact]
    public void Dme_counts_down_so_live_updates_are_visible()
    {
        using var test = Start();
        var before = test.State;
        Assert.False(double.IsNaN(before.Dme2Nm)); // both DME windows show something

        _time.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(before.Dme1Nm - 0.3, test.State.Dme1Nm, 3);
        Assert.Equal(before.Dme2Nm + 0.3, test.State.Dme2Nm, 3);
    }
}

public class SimRouterFallbackTests
{
    [Fact]
    public void Test_mode_steps_aside_for_a_real_simulator_and_returns_after_it()
    {
        var xplane = new FakeSim("X-Plane");
        var test = new FakeSim("Test mode");
        using var router = new SimRouter([xplane], test);

        test.Connect(RadioState.Empty with { Squawk = 7000 });
        Assert.Equal("Test mode", router.Name);

        xplane.Connect(RadioState.Empty with { Squawk = 1200 });
        Assert.Equal("X-Plane", router.Name);
        router.Send(new SetSquawk(2000));
        Assert.Single(xplane.Sent);
        Assert.Empty(test.Sent);

        xplane.Disconnect();
        Assert.Equal("Test mode", router.Name);
        Assert.Equal(7000, router.State.Squawk);
    }
}

public class CurrentInputReportTests
{
    [Theory]
    [InlineData(new byte[] { 0, 0x01, 0x01, 0 }, true)]  // upper COM1, lower COM2 (real panel readback)
    [InlineData(new byte[] { 0, 0x40, 0x00, 0 }, true)]  // upper XPDR, lower between positions
    [InlineData(new byte[] { 0, 0x00, 0x00, 0 }, false)] // nothing set: not a real state
    [InlineData(new byte[] { 0, 0x03, 0x00, 0 }, false)] // two upper positions at once
    public void Accepts_only_plausible_panel_states(byte[] report, bool plausible) =>
        Assert.Equal(plausible, CurrentInputReport.Plausible(report));
}

public sealed class PanelActivityTests : IDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly FakePanel _panel = new();
    private readonly FakeSim _sim = new();
    private readonly TempSettings _settings = new();
    private readonly PanelController _controller;

    public PanelActivityTests()
    {
        _controller = new PanelController(_panel, _sim, _settings.Store, _time);
        _controller.Start();
    }

    public void Dispose()
    {
        _controller.Dispose();
        _settings.Dispose();
    }

    [Fact]
    public void Knob_turns_and_button_presses_show_even_without_a_simulator()
    {
        _panel.Raise(new KnobTurned(PanelRow.Upper, Knob.Inner, 1));
        _panel.Raise(new KnobTurned(PanelRow.Upper, Knob.Inner, 1));
        _panel.Raise(new KnobTurned(PanelRow.Lower, Knob.Outer, -1));
        _panel.Raise(new ButtonChanged(PanelRow.Lower, true));

        Assert.Equal(new RowActivity(2, 0, false, false), _controller.View.UpperActivity);
        Assert.Equal(new RowActivity(0, -1, true, false), _controller.View.LowerActivity);

        _time.Advance(TimeSpan.FromMilliseconds(700));
        Assert.True(_controller.View.LowerActivity.LongPress);

        _panel.Raise(new ButtonChanged(PanelRow.Lower, false));
        Assert.Equal(new RowActivity(0, -1, false, false), _controller.View.LowerActivity);
    }
}

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RadioBridgeTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Older_acceleration_values_give_way_to_the_gentler_com_defaults()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """
            {
              "BaroUnit": "InchesOfMercury",
              "Acceleration": { "Enabled": true, "FastDetentMs": 35, "MediumDetentMs": 90, "FastMultiplier": 8, "MediumMultiplier": 3 }
            }
            """);

        var settings = new RadioBridge.Core.Settings.SettingsStore(path).Current;

        Assert.Equal(new RadioBridge.Core.Radio.AccelerationSettings(), settings.ComAcceleration);
        Assert.Equal((20, 3, 40, 2), (settings.ComAcceleration.FastDetentMs, settings.ComAcceleration.FastMultiplier,
            settings.ComAcceleration.MediumDetentMs, settings.ComAcceleration.MediumMultiplier));
        Assert.Equal(RadioBridge.Core.Radio.BaroUnit.InchesOfMercury, settings.BaroUnit); // the rest is kept
    }
}
