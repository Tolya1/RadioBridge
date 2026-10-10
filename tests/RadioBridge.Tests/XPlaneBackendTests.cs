using System.Text.Json;
using Microsoft.Extensions.Logging;
using RadioBridge.Core.Sim;
using RadioBridge.Core.Sim.XPlane;
using RadioBridge.FakeXPlane;

namespace RadioBridge.Tests;

/// <summary>
/// Runs the backend against the X-Plane emulator, which follows the documented Web API v2 protocol:
/// id lookup over REST, then subscribe / update / set / command messages over the WebSocket.
/// </summary>
public sealed class XPlaneBackendTests : IAsyncLifetime
{
    private readonly XPlaneEmulator _xplane = new();
    private XPlaneBackend _backend = null!;

    public async Task InitializeAsync()
    {
        await _xplane.StartAsync();
        _backend = new XPlaneBackend(() => _xplane.Port);
        _backend.Start();
        await Wait.Until(() => _backend.IsConnected && _backend.State.Squawk >= 0, "the backend to connect and receive values");
    }

    public async Task DisposeAsync()
    {
        _backend.Dispose();
        await _xplane.DisposeAsync();
    }

    private async Task<JsonElement> NextMessageAsync(string type)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var message = await _xplane.Received.ReadAsync(timeout.Token);
            if (message.GetProperty("type").GetString() == type)
                return message;
        }
    }

    [Fact]
    public async Task Subscribes_to_the_radio_datarefs()
    {
        var subscribe = await NextMessageAsync("dataref_subscribe_values");

        var ids = subscribe.GetProperty("params").GetProperty("datarefs").EnumerateArray().Select(d => d.GetProperty("id").GetInt64()).ToHashSet();
        Assert.Equal(XPlaneNames.Datarefs.Count, ids.Count);
        foreach (var name in XPlaneNames.Datarefs)
            Assert.Contains(_xplane.IdOf(name), ids);
    }

    [Fact]
    public void Reads_initial_values_in_radio_state_units()
    {
        var s = _backend.State;
        Assert.Equal(new RadioPair(118_000, 121_500), s.Com1);
        Assert.Equal(new RadioPair(110_300, 113_500), s.Nav1);
        Assert.Equal(362, s.Adf1Khz);
        Assert.Equal(7000, s.Squawk);
        Assert.Equal(1013.2, s.BaroHpa, 1);
        Assert.Equal(12.3, s.Dme1Nm, 3);
    }

    [Fact]
    public async Task Follows_changes_made_in_the_cockpit()
    {
        _xplane["sim/cockpit2/radios/actuators/com2_standby_frequency_hz_833"] = 124_355;

        await Wait.Until(() => _backend.State.Com2.StandbyKhz == 124_355, "the pushed change");
    }

    [Fact]
    public async Task Sets_frequencies_with_integer_values_in_dataref_units()
    {
        _backend.Send(new SetStandby(Tunable.Com1, 118_010));
        var set = await NextMessageAsync("dataref_set_values");
        var item = set.GetProperty("params").GetProperty("datarefs")[0];
        Assert.Equal(_xplane.IdOf("sim/cockpit2/radios/actuators/com1_standby_frequency_hz_833"), item.GetProperty("id").GetInt64());
        Assert.Equal("118010", item.GetProperty("value").GetRawText());

        _backend.Send(new SetStandby(Tunable.Nav2, 110_350));
        await Wait.Until(() => _xplane["sim/cockpit2/radios/actuators/nav2_standby_frequency_hz"] == 11_035, "NAV2 standby in X-Plane");
        await Wait.Until(() => _backend.State.Nav2.StandbyKhz == 110_350, "NAV2 standby to come back");
    }

    [Fact]
    public async Task Sets_squawk_adf_and_altimeter()
    {
        _backend.Send(new SetSquawk(1200));
        _backend.Send(new SetAdf(2, 415));
        _backend.Send(new SetBaro(1000));

        await Wait.Until(() => _backend.State is { Squawk: 1200, Adf2Khz: 415 } s && Math.Abs(s.BaroHpa - 1000) < 0.1, "the values to round-trip");
        Assert.Equal(29.53, _xplane["sim/cockpit2/gauges/actuators/barometer_setting_in_hg_pilot"], 2);

        _backend.Send(new SetBaroStandard());
        await Wait.Until(() => Math.Abs(_backend.State.BaroHpa - 1013.2) < 0.1, "standard pressure");
    }

    [Fact]
    public async Task Swap_runs_the_flip_command_once()
    {
        _backend.Send(new SwapRadio(Tunable.Com1));

        var message = await NextMessageAsync("command_set_is_active");
        var command = message.GetProperty("params").GetProperty("commands")[0];
        Assert.Equal(_xplane.IdOf("sim/radios/com1_standy_flip"), command.GetProperty("id").GetInt64());
        Assert.Equal(0, command.GetProperty("duration").GetInt32());
        await Wait.Until(() => _backend.State.Com1 == new RadioPair(121_500, 118_000), "the swapped radio");
    }

    [Fact]
    public async Task Reconnects_after_xplane_restarts()
    {
        await _xplane.StopAsync();
        await Wait.Until(() => !_backend.IsConnected, "the backend to notice");
        Assert.Equal(RadioState.Empty, _backend.State);

        await _xplane.StartAsync();
        await Wait.Until(() => _backend.IsConnected && _backend.State.Squawk == 7000, "the backend to reconnect", timeoutMs: 10_000);
    }
}

public class XPlaneDiagnosticsTests
{
    [Fact]
    public async Task Another_program_on_the_port_is_not_mistaken_for_an_old_xplane()
    {
        // A web server that answers 404 to everything, like InfluxDB, whose default port is also 8086.
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        Microsoft.AspNetCore.Hosting.HostingAbstractionsWebHostBuilderExtensions.UseUrls(builder.WebHost, "http://127.0.0.1:0");
        await using var other = builder.Build();
        await other.StartAsync();
        int port = new Uri(other.Urls.First()).Port;

        var log = new ListLogger<XPlaneBackend>();
        using var backend = new XPlaneBackend(() => port, log);
        backend.Start();

        await Wait.Until(() => log.Messages.Any(m => m.Contains("Something other than X-Plane")), "the warning");
        Assert.False(backend.IsConnected);
        Assert.DoesNotContain(log.Messages, m => m.Contains("12.1.4 or newer"));
    }
}
