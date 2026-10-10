using System.Text.Json;
using RadioBridge.Core.Panel;
using static RadioBridge.Core.Panel.PanelRow;

namespace RadioBridge.FunctionalTests;

/// <summary>
/// The debug recording of the running app: what a user would send with a bug report has to tell the whole
/// story, from the panel's input through RadioBridge's decisions to what went to the simulator and back.
/// </summary>
[Collection("app")]
public sealed class DebugRecordingTests(AppFixture fx) : IAsyncLifetime
{
    private AppClient App => fx.App;

    public Task InitializeAsync() => fx.ResetSettingsAsync();

    public async Task DisposeAsync() => await App.RecordAsync(false);

    [Fact]
    public async Task A_recording_tells_the_whole_story_of_a_knob_turn_and_a_swap()
    {
        await App.SelectAsync(Upper, Selector.Com1);
        await App.WaitForAsync(s => s.Sim.Connected && s.Sim.State.Com1.StandbyKhz > 0, "the simulator's radios");
        var started = await App.RecordAsync(true);
        Assert.True(started.Active);

        // Tune one channel up and back and swap twice, so a live simulator ends where it started.
        await App.TurnAsync(Upper, Knob.Inner, +1);
        await App.TurnAsync(Upper, Knob.Inner, -1);
        await App.PressAsync(Upper);
        await Task.Delay(600);
        await App.PressAsync(Upper);
        await Task.Delay(600);
        var stopped = await App.RecordAsync(false);

        Assert.False(stopped.Active);
        Assert.Equal(started.Path, stopped.Path);
        var events = File.ReadAllLines(stopped.Path!).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
        IEnumerable<JsonElement> Of(string src, string ev) =>
            events.Where(e => e.GetProperty("src").GetString() == src && e.GetProperty("ev").GetString() == ev);

        var header = events[0];
        Assert.Equal("start", header.GetProperty("ev").GetString());
        Assert.False(string.IsNullOrEmpty(header.GetProperty("app").GetString()));
        Assert.Equal(fx.ExpectedSimName, header.GetProperty("simulator").GetString());
        Assert.True(header.GetProperty("settings").TryGetProperty("comSpacing", out _));
        Assert.Equal("stop", events[^1].GetProperty("ev").GetString());

        Assert.Equal(2, Of("panel", "input").Count(e => e.TryGetProperty("knob", out _)));
        Assert.Equal(4, Of("panel", "input").Count(e => e.TryGetProperty("button", out _))); // down and up, twice
        Assert.Equal(2, Of("controller", "tune").Count());
        Assert.Equal(2, Of("controller", "swap").Count());
        Assert.NotEmpty(Of("controller", "displays"));
        Assert.NotEmpty(Of("sim", "state"));
        Assert.Contains(Of("log", "debug"), e => e.GetProperty("message").GetString()!.Contains("Swap"));

        if (fx.Mode == SimMode.Msfs)
        {
            Assert.Equal(2, Of("msfs", "event").Count(e => e.GetProperty("event").GetString() == "COM1_RADIO_SWAP"));
        }
        else
        {
            Assert.Equal(2, Of("xplane", "set").Count());
            Assert.Equal(2, Of("xplane", "command").Count(e => e.GetProperty("command").GetString() == "sim/radios/com1_standy_flip"));
            Assert.NotEmpty(Of("xplane", "watching"));
            Assert.Contains(Of("xplane", "values"), e => e.GetProperty("values").TryGetProperty("sim/cockpit2/radios/actuators/com1_frequency_hz_833", out _));
        }
    }

    [Fact]
    public async Task The_state_shows_whether_a_recording_runs()
    {
        await App.RecordAsync(true);
        Assert.True((await App.StateAsync()).Recording!.Active);

        await App.RecordAsync(false);
        var state = await App.StateAsync();
        Assert.False(state.Recording!.Active);
        Assert.True(state.Recording.Bytes > 0);
        Assert.StartsWith(Path.Combine(fx.RunDirectory, "recordings"), state.Recording.Path);
    }
}
