using System.Text.Json;
using Microsoft.Extensions.Logging;
using RadioBridge.Core.Radio;
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

    private async Task<double> WrittenBaroAsync()
    {
        var set = await NextMessageAsync("dataref_set_values");
        var item = set.GetProperty("params").GetProperty("datarefs")[0];
        Assert.Equal(_xplane.IdOf("sim/cockpit2/gauges/actuators/barometer_setting_in_hg_pilot"), item.GetProperty("id").GetInt64());
        return item.GetProperty("value").GetDouble();
    }

    [Fact]
    public async Task Altimeter_settings_are_written_exactly_with_a_margin_for_truncating_cockpits()
    {
        _backend.Send(new SetBaro(1040)); // a whole hPa, as the hPa knob sets it: 30.7112 inHg, like X-Plane's own knob
        Assert.Equal(1040 / DisplayFormat.HpaPerInHg + XPlaneNames.BaroMarginInHg, await WrittenBaroAsync(), 9);

        _backend.Send(new SetBaro(29.91 * DisplayFormat.HpaPerInHg)); // as the inHg knob sets it
        Assert.Equal(29.91 + XPlaneNames.BaroMarginInHg, await WrittenBaroAsync(), 9);
    }

    [Fact]
    public async Task Standard_pressure_is_written_as_2992_instead_of_the_internal_2992125()
    {
        _backend.Send(new SetBaroStandard());

        // A write, not X-Plane's command, which leaves 29.92125.
        Assert.Equal(29.92 + XPlaneNames.BaroMarginInHg, await WrittenBaroAsync(), 9);
        await Wait.Until(() => Math.Abs(_xplane["sim/cockpit2/gauges/actuators/barometer_setting_in_hg_pilot"] - 29.92) < 0.0001, "29.92 in X-Plane");
    }

    [Fact]
    public async Task Swap_presses_the_flip_command_like_a_quick_click()
    {
        _backend.Send(new SwapRadio(Tunable.Com1));

        var message = await NextMessageAsync("command_set_is_active");
        var command = message.GetProperty("params").GetProperty("commands")[0];
        Assert.Equal(_xplane.IdOf("sim/radios/com1_standy_flip"), command.GetProperty("id").GetInt64());
        Assert.True(command.GetProperty("is_active").GetBoolean());
        Assert.Equal(0.15, command.GetProperty("duration").GetDouble()); // held, so radios that act on release see a press
        await Wait.Until(() => _backend.State.Com1 == new RadioPair(121_500, 118_000), "the swapped radio");
    }

    [Fact]
    public async Task Swap_works_on_radios_that_flip_when_the_press_ends()
    {
        _xplane.FlipOnRelease = true; // REP's KX165 in the SF-260

        _backend.Send(new SwapRadio(Tunable.Com2));

        await Wait.Until(() => _backend.State.Com2 == new RadioPair(119_250, 122_800), "COM2 to swap on release");
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
        Assert.Null(backend.Notice); // another program isn't certainly X-Plane's problem: log only, no banner
    }
}

public sealed class XPlaneNoticeTests : IAsyncLifetime
{
    private readonly XPlaneEmulator _xplane = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _xplane.DisposeAsync();

    private async Task<XPlaneBackend> StartBackendAsync()
    {
        await _xplane.StartAsync();
        var backend = new XPlaneBackend(() => _xplane.Port);
        backend.Start();
        return backend;
    }

    [Fact]
    public async Task X_plane_that_refuses_connections_explains_which_setting_to_change()
    {
        _xplane.RefuseConnections = true;
        using var backend = await StartBackendAsync();

        await Wait.Until(() => backend.Notice is not null, "the notice");

        Assert.Contains("Disable incoming traffic", backend.Notice!.Text);
        Assert.False(backend.Notice.CanLocateSimConnect);
        Assert.False(backend.IsConnected);
    }

    [Fact]
    public async Task Too_old_x_plane_says_which_version_is_needed()
    {
        _xplane.Api1Only = true;
        using var backend = await StartBackendAsync();

        await Wait.Until(() => backend.Notice is not null, "the notice");

        Assert.Contains("12.1.4", backend.Notice!.Text);
    }

    [Fact]
    public async Task The_notice_goes_away_once_the_problem_is_fixed()
    {
        _xplane.RefuseConnections = true;
        using var backend = await StartBackendAsync();
        await Wait.Until(() => backend.Notice is not null, "the notice");

        _xplane.RefuseConnections = false;

        await Wait.Until(() => backend.IsConnected, "the backend to connect", timeoutMs: 10_000);
        Assert.Null(backend.Notice);
    }

    [Fact]
    public async Task X_plane_not_running_is_not_a_problem()
    {
        using var backend = new XPlaneBackend(() => 1); // nothing listens there
        backend.Start();
        await Task.Delay(800);

        Assert.Null(backend.Notice);
    }
}

public sealed class XPlaneWriteLoggingTests : IAsyncLifetime
{
    private readonly XPlaneEmulator _xplane = new();
    private readonly ListLogger<XPlaneBackend> _log = new();
    private XPlaneBackend _backend = null!;

    public async Task InitializeAsync()
    {
        await _xplane.StartAsync();
        _backend = new XPlaneBackend(() => _xplane.Port, _log);
        _backend.Start();
        await Wait.Until(() => _backend.IsConnected && _backend.State.Squawk >= 0, "the backend to connect");
    }

    public async Task DisposeAsync()
    {
        _backend.Dispose();
        await _xplane.DisposeAsync();
    }

    [Fact]
    public async Task Every_write_and_command_leaves_a_line_in_the_log()
    {
        _backend.Send(new SetBaro(1040));
        _backend.Send(new SetBaroStandard());
        _backend.Send(new SetSquawk(1200));

        await Wait.Until(() => _log.Messages.Count(m => m.StartsWith("X-Plane <-")) >= 3, "the log lines");
        Assert.Contains(_log.Messages, m => m.Contains("barometer_setting_in_hg_pilot = 30.711"));
        Assert.Contains(_log.Messages, m => m.Contains("barometer_setting_in_hg_pilot = 29.92005"));
        Assert.Contains(_log.Messages, m => m.Contains("transponder_code") && m.EndsWith("1200"));
    }
}

/// <summary>The emulator's model of a radio that flips on release, which the swap tests rely on.</summary>
public sealed class XPlaneEmulatorFlipTests : IAsyncLifetime
{
    private readonly XPlaneEmulator _xplane = new() { FlipOnRelease = true };

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
    public async Task A_press_that_starts_and_ends_at_once_does_nothing_but_a_held_one_flips_on_release()
    {
        using var socket = new System.Net.WebSockets.ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_xplane.Port}/api/v2"), CancellationToken.None);
        const string Active = "sim/cockpit2/radios/actuators/com1_frequency_hz_833";

        await PressAsync(socket, "sim/radios/com1_standy_flip", 0);
        await Task.Delay(300);
        Assert.Equal(118_000, _xplane[Active]);

        await PressAsync(socket, "sim/radios/com1_standy_flip", 0.15);
        Assert.Equal(118_000, _xplane[Active]); // still held
        await Wait.Until(() => _xplane[Active] == 121_500, "the flip on release");
    }
}
