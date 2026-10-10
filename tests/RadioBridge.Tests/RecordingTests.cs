using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using RadioBridge.Core;
using RadioBridge.Core.Diagnostics;
using RadioBridge.Core.Panel;
using RadioBridge.Core.Sim;
using RadioBridge.Core.Sim.XPlane;
using RadioBridge.FakeXPlane;

namespace RadioBridge.Tests;

/// <summary>A folder for recordings, deleted with the test.</summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RadioBridgeTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }

    /// <summary>Every line of a recording, parsed; fails if one isn't a JSON object.</summary>
    public static List<JsonElement> ReadEvents(string file) =>
        ReadText(file).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToList();

    /// <summary>Reads a recording, also while it is still being written.</summary>
    public static string ReadText(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

internal static class EventExtensions
{
    public static IEnumerable<JsonElement> Where(this IEnumerable<JsonElement> events, string src, string ev) =>
        events.Where(e => e.GetProperty("src").GetString() == src && e.GetProperty("ev").GetString() == ev);
}

public sealed class RecorderTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Writes_one_json_object_per_line_with_time_source_and_event()
    {
        using var recorder = new Recorder();
        string file = recorder.Start(_folder.Path, w => w.WriteString("app", "test"));
        recorder.Record("panel", "input", w => w.WriteString("row", "Upper"));
        recorder.Stop();

        var events = TempFolder.ReadEvents(file);
        Assert.Equal(new[] { "start", "input", "stop" }, events.Select(e => e.GetProperty("ev").GetString()!));
        Assert.Equal("test", events[0].GetProperty("app").GetString());
        Assert.Equal("Upper", events[1].GetProperty("row").GetString());
        Assert.All(events, e =>
        {
            Assert.True(e.GetProperty("ms").GetDouble() >= 0);
            Assert.Matches(@"^\d\d:\d\d:\d\d\.\d{3}$", e.GetProperty("t").GetString());
        });
        Assert.False(recorder.IsRecording);
        Assert.Equal(file, recorder.FilePath);
        Assert.Equal(new FileInfo(file).Length, recorder.BytesWritten);
    }

    [Fact]
    public void Records_nothing_and_creates_no_file_while_off()
    {
        using var recorder = new Recorder();
        bool called = false;

        recorder.Record("panel", "input", _ => called = true);

        Assert.False(called);
        Assert.False(Directory.Exists(_folder.Path));
    }

    [Fact]
    public void Values_json_cannot_hold_become_null_instead_of_losing_the_event()
    {
        using var recorder = new Recorder();
        string file = recorder.Start(_folder.Path);
        recorder.Record("sim", "state", w => w.WriteRadioState("radios", RadioState.Empty)); // no DME, no altimeter: NaN
        recorder.Stop();

        var radios = TempFolder.ReadEvents(file)[1].GetProperty("radios");
        Assert.Equal(JsonValueKind.Null, radios.GetProperty("dme1").ValueKind);
        Assert.Equal(JsonValueKind.Null, radios.GetProperty("baroHpa").ValueKind);
        Assert.Equal(-1, radios.GetProperty("squawk").GetInt32());
    }

    [Fact]
    public async Task Stops_by_itself_at_the_size_limit()
    {
        using var recorder = new Recorder(maxBytes: 10_000);
        string file = recorder.Start(_folder.Path);

        for (int i = 0; i < 1000 && recorder.IsRecording; i++)
        {
            recorder.Record("panel", "report", w => w.WriteString("bytes", new string('0', 100)));
            await Task.Delay(1);
        }

        await Wait.Until(() => !recorder.IsRecording, "the recording to stop");
        var last = TempFolder.ReadEvents(file)[^1];
        Assert.Equal("stop", last.GetProperty("ev").GetString());
        Assert.Contains("MB", last.GetProperty("reason").GetString());
    }

    [Fact]
    public void A_second_start_keeps_the_running_recording_and_a_new_one_gets_its_own_file()
    {
        using var recorder = new Recorder();
        string first = recorder.Start(_folder.Path);
        Assert.Equal(first, recorder.Start(_folder.Path));
        recorder.Stop();

        string second = recorder.Start(_folder.Path);
        recorder.Stop();

        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first) && File.Exists(second));
    }

    [Fact]
    public void Only_the_newest_recordings_are_kept_and_nothing_else_in_the_folder_is_touched()
    {
        Directory.CreateDirectory(_folder.Path);
        var start = DateTime.UtcNow.AddDays(-1);
        for (int i = 0; i < 6; i++)
        {
            string old = Path.Combine(_folder.Path, $"radiobridge-old{i}.jsonl");
            File.WriteAllText(old, "{}\n");
            File.SetLastWriteTimeUtc(old, start.AddMinutes(i)); // old5 is the newest
        }
        string notes = Path.Combine(_folder.Path, "notes.txt");
        File.WriteAllText(notes, "mine");

        using var recorder = new Recorder(keepFiles: 4);
        string file = recorder.Start(_folder.Path);
        recorder.Stop();

        var kept = Directory.GetFiles(_folder.Path, "radiobridge-*.jsonl").Select(Path.GetFileName).Order().ToList();
        Assert.Equal(new[] { Path.GetFileName(file), "radiobridge-old3.jsonl", "radiobridge-old4.jsonl", "radiobridge-old5.jsonl" }.Order(), kept);
        Assert.True(File.Exists(notes));
        Assert.Equal(3, TempFolder.ReadEvents(file)[0].GetProperty("removedOldRecordings").GetInt32());
    }

    [Fact]
    public void Log_messages_are_copied_into_the_recording_only_while_it_runs()
    {
        using var recorder = new Recorder();
        using var loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(new RecorderLoggerProvider(recorder)));
        var log = loggers.CreateLogger<XPlaneBackend>();

        log.LogInformation("before");
        string file = recorder.Start(_folder.Path);
        log.LogDebug("X-Plane <- {Name} = {Value}", "transponder_code", 1200);
        log.LogTrace("too chatty");
        recorder.Stop();
        log.LogInformation("after");

        var logs = TempFolder.ReadEvents(file).Where(e => e.GetProperty("src").GetString() == "log").ToList();
        var line = Assert.Single(logs);
        Assert.Equal("debug", line.GetProperty("ev").GetString());
        Assert.Equal("XPlaneBackend", line.GetProperty("category").GetString());
        Assert.Equal("X-Plane <- transponder_code = 1200", line.GetProperty("message").GetString());
    }
}

public sealed class XPlaneWatchListTests
{
    [Theory]
    [InlineData("sim/cockpit2/radios/actuators/com1_power", "int")]
    [InlineData("sim/cockpit/radios/com1_stdby_freq_hz", "int")]
    [InlineData("sim/cockpit2/gauges/actuators/barometer_setting_in_hg_copilot", "float")]
    [InlineData("sim/cockpit2/electrical/avionics_on", "int")]
    [InlineData("sim/aircraft/view/acf_relative_path", "data")]
    [InlineData("simcoders/rep/avionics/bendixking/kx165atso/com_2_swap_press_ratio", "float")]
    [InlineData("simcoders/rep/avionics/bendixking/kx165atso/small_com_1_knob_pulled", "int")]
    [InlineData("simcoders/rep/settings/pilot_use_altimeter_inhg", "int")]
    [InlineData("thranda/instruments/BaroUnits", "int")]
    [InlineData("TDS/radios/indicators/gtnxi_unit2", "int")]
    public void Watches_radio_settings_of_xplane_and_the_add_ons(string name, string type) =>
        Assert.True(XPlaneWatchList.Wanted(name, type), name);

    [Theory]
    [InlineData("sim/flightmodel/position/latitude", "double")]
    [InlineData("sim/cockpit2/radios/indicators/nav1_hdef_dots_pilot", "float")] // moves all the time in flight
    [InlineData("TDS/radios/indicators/hsi_hdef_dots_1", "float")]
    [InlineData("simcoders/rep/avionics/bendixking/kx165atso/com_1_volume", "float")]
    [InlineData("xpilot/com1_station_callsign", "data")]
    [InlineData("librain/glass_0/thrust", "float")]
    public void Leaves_out_everything_else(string name, string type) =>
        Assert.False(XPlaneWatchList.Wanted(name, type), name);

    [Fact]
    public void Leaves_out_what_radiobridge_already_subscribes_to()
    {
        var selected = XPlaneWatchList.Select(
            [("sim/cockpit2/radios/actuators/com1_frequency_hz_833", "int"), ("sim/cockpit2/radios/actuators/com1_power", "int")],
            XPlaneNames.Datarefs);

        Assert.Equal(["sim/cockpit2/radios/actuators/com1_power"], selected);
    }
}

public sealed class ControllerRecordingTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly TempSettings _settings = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-10T12:00:00Z"));
    private readonly FakePanel _panel = new();
    private readonly FakeSim _sim = new("X-Plane");
    private readonly Recorder _recorder = new();
    private readonly PanelController _controller;

    public ControllerRecordingTests()
    {
        _controller = new PanelController(_panel, _sim, _settings.Store, _time, recorder: _recorder);
        _controller.Start();
    }

    public void Dispose()
    {
        _controller.Dispose();
        _recorder.Dispose();
        _settings.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public void Records_input_decisions_displays_and_values_the_sim_did_not_take()
    {
        string file = _recorder.Start(_folder.Path);
        _sim.Connect(new RadioState { Com1 = new RadioPair(118_000, 121_500) });
        _panel.Raise(new SelectorMoved(PanelRow.Upper, Selector.Com1));
        _panel.Raise(new KnobTurned(PanelRow.Upper, Knob.Inner, 1));
        _time.Advance(TimeSpan.FromSeconds(1)); // the sim never confirms 121.505
        _panel.Raise(new ButtonChanged(PanelRow.Upper, true));
        _time.Advance(TimeSpan.FromMilliseconds(100));
        _panel.Raise(new ButtonChanged(PanelRow.Upper, false));
        _recorder.Stop();

        var events = TempFolder.ReadEvents(file);
        Assert.Contains(events.Where("panel", "input"), e => e.TryGetProperty("knob", out var k) && k.GetString() == "Inner");
        var tune = Assert.Single(events.Where("controller", "tune"));
        Assert.Equal((121_500, 121_505), (tune.GetProperty("from").GetInt32(), tune.GetProperty("to").GetInt32()));
        Assert.Equal("Com1", tune.GetProperty("selector").GetString());
        var unconfirmed = Assert.Single(events.Where("controller", "unconfirmed"));
        Assert.Equal(("Com1", "121505", "121500"),
            (unconfirmed.GetProperty("what").GetString(), unconfirmed.GetProperty("sent").GetString(), unconfirmed.GetProperty("simulator").GetString()));
        var swap = Assert.Single(events.Where("controller", "swap"));
        Assert.Equal(121_500, swap.GetProperty("active").GetInt32());
        Assert.Contains(events.Where("controller", "displays"), e => e.GetProperty("upperRight").GetString() == "21.505");
    }
}

public sealed class XPlaneRecordingTests : IAsyncLifetime
{
    private const string RepSwap = "simcoders/rep/avionics/bendixking/kx165atso/com_1_swap_press_ratio";
    private const string Noise = "TDS/radios/indicators/hsi_hdef_dots_1";

    private readonly TempFolder _folder = new();
    private readonly XPlaneEmulator _xplane = new();
    private readonly Recorder _recorder = new();
    private XPlaneBackend _backend = null!;

    public async Task InitializeAsync()
    {
        _xplane.AddDataref(RepSwap, "float", 0);
        _xplane.AddDataref(Noise, "float", 0.25);
        await _xplane.StartAsync();
        _backend = new XPlaneBackend(() => _xplane.Port, recorder: _recorder);
        _backend.Start();
        await Wait.Until(() => _backend.IsConnected && _backend.State.Squawk >= 0, "the backend to connect");
    }

    public async Task DisposeAsync()
    {
        _backend.Dispose();
        _recorder.Dispose();
        await _xplane.DisposeAsync();
        _folder.Dispose();
    }

    [Fact]
    public async Task A_recording_started_mid_flight_watches_the_add_ons_and_names_every_value()
    {
        string file = _recorder.Start(_folder.Path);
        await Wait.Until(() => TempFolder.ReadText(file).Contains("\"watching\""), "the extra datarefs to be watched");

        _backend.Send(new SetStandby(Tunable.Com1, 121_505));
        _backend.Send(new SwapRadio(Tunable.Com1));
        _xplane[RepSwap] = 1; // the add-on animating its button
        await Wait.Until(() => TempFolder.ReadText(file).Contains(RepSwap + "\":1"), "the add-on's value in the recording");
        _recorder.Stop();

        var events = TempFolder.ReadEvents(file);
        var watching = Assert.Single(events.Where("xplane", "watching")).GetProperty("datarefs");
        Assert.True(watching.TryGetProperty(RepSwap, out _));
        Assert.False(watching.TryGetProperty(Noise, out _));

        var set = Assert.Single(events.Where("xplane", "set"));
        Assert.Equal("sim/cockpit2/radios/actuators/com1_standby_frequency_hz_833", set.GetProperty("dataref").GetString());
        Assert.Equal(121_505, set.GetProperty("value").GetInt32());
        var command = Assert.Single(events.Where("xplane", "command"));
        Assert.Equal("sim/radios/com1_standy_flip", command.GetProperty("command").GetString());
        Assert.Equal(0.15, command.GetProperty("pressSeconds").GetDouble());
        Assert.Contains(events.Where("xplane", "result"), r => r.GetProperty("req").GetString() == command.GetProperty("req").GetInt32().ToString()
                                                               && r.GetProperty("success").GetBoolean());

        var values = events.Where("xplane", "values").Select(e => e.GetProperty("values")).ToList();
        Assert.Contains(values, v => v.TryGetProperty("sim/cockpit2/radios/actuators/com1_frequency_hz_833", out var a) && a.GetInt32() == 121_505);
        Assert.DoesNotContain(values, v => v.EnumerateObject().Any(p => p.Name.StartsWith("id:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Stopping_the_recording_drops_the_extra_datarefs()
    {
        _recorder.Start(_folder.Path);
        await Wait.Until(() => TempFolder.ReadText(_recorder.FilePath!).Contains("\"watching\""), "the extra datarefs to be watched");

        _recorder.Stop();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var message = await _xplane.Received.ReadAsync(timeout.Token);
            if (message.GetProperty("type").GetString() == "dataref_unsubscribe_values")
            {
                var ids = message.GetProperty("params").GetProperty("datarefs").EnumerateArray().Select(d => d.GetProperty("id").GetInt64());
                Assert.Contains(_xplane.IdOf(RepSwap), ids);
                break;
            }
        }
    }
}
