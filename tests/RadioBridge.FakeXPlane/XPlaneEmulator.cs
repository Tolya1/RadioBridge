using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RadioBridge.FakeXPlane;

/// <summary>
/// An emulation of the parts of X-Plane 12's Web API (v2) that radio tools use, with a small model
/// of the radios behind it. Dataref names come from X-Plane's DataRefs.txt and are defined here
/// independently of RadioBridge, so the two can catch each other's mistakes.
/// <para>Modelled behaviour: COM radios snap to 8.33 or (when <see cref="Com833Capable"/> is off)
/// 25 kHz channels; the *_hz_833 and 10 kHz COM datarefs stay in sync; flip commands swap active and
/// standby; subscribers get all values first and then only changes, about ten times a second.</para>
/// </summary>
public sealed class XPlaneEmulator : IAsyncDisposable
{
    private const string Act = "sim/cockpit2/radios/actuators/";

    private readonly Dictionary<string, Dataref> _byName = new();
    private readonly Dictionary<long, Dataref> _byId = new();
    private readonly Dictionary<string, long> _commands = new();
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<Client, byte> _clients = new();
    private readonly Channel<JsonElement> _received = Channel.CreateUnbounded<JsonElement>();
    private WebApplication? _app;
    private CancellationTokenSource? _pushCts;
    private Task? _pushLoop;

    public XPlaneEmulator(int port = 0)
    {
        Port = port;
        long id = 4000;
        void Add(string name, string type, double value) => _byName[name] = _byId[id += 3] = new Dataref(id, name, type, value);

        foreach (int n in (int[])[1, 2])
        {
            Add($"{Act}com{n}_frequency_hz_833", "int", n == 1 ? 118_000 : 122_800);
            Add($"{Act}com{n}_standby_frequency_hz_833", "int", n == 1 ? 121_500 : 119_250);
            Add($"{Act}com{n}_frequency_hz", "int", n == 1 ? 11_800 : 12_280);
            Add($"{Act}com{n}_standby_frequency_hz", "int", n == 1 ? 12_150 : 11_925);
            Add($"{Act}nav{n}_frequency_hz", "int", n == 1 ? 11_030 : 11_390);
            Add($"{Act}nav{n}_standby_frequency_hz", "int", n == 1 ? 11_350 : 10_800);
            Add($"{Act}adf{n}_frequency_hz", "int", n == 1 ? 362 : 1_160);
            Add($"{Act}adf{n}_standby_frequency_hz", "int", 300);
            Add($"sim/cockpit2/radios/indicators/nav{n}_dme_distance_nm", "float", n == 1 ? 12.3 : 0);
        }
        Add($"{Act}transponder_code", "int", 7000);
        Add("sim/cockpit2/gauges/actuators/barometer_setting_in_hg_pilot", "float", 29.92);

        foreach (var name in (string[])["sim/radios/com1_standy_flip", "sim/radios/com2_standy_flip", "sim/radios/nav1_standy_flip",
                     "sim/radios/nav2_standy_flip", "sim/instruments/barometer_2992", "sim/instruments/barometer_std"])
            _commands[name] = id += 3;
    }

    public int Port { get; private set; }

    /// <summary>When false, COM radios behave like 25 kHz-only radios and snap 8.33 channels.</summary>
    public bool Com833Capable { get; set; } = true;

    public string Version { get; set; } = "12.4.4-emulated";

    /// <summary>Behave like X-Plane 12.1.1-12.1.3: only the v1 API, no capabilities endpoint.</summary>
    public bool Api1Only { get; set; }

    /// <summary>Behave like X-Plane with "Disable incoming traffic" on: every request gets 403.</summary>
    public bool RefuseConnections { get; set; }

    public bool IsRunning => _app is not null;

    public int ConnectedClients => _clients.Count;

    /// <summary>Every WebSocket message received from clients, parsed.</summary>
    public ChannelReader<JsonElement> Received => _received.Reader;

    public long IdOf(string nameOrCommand) =>
        _byName.TryGetValue(nameOrCommand, out var d) ? d.Id : _commands[nameOrCommand];

    /// <summary>Reads a dataref, or changes it as if the pilot did so in the cockpit.</summary>
    public double this[string dataref]
    {
        get { lock (_gate) return _byName[dataref].Value; }
        set { lock (_gate) Write(_byName[dataref], value); }
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{Port}");
        var app = builder.Build();
        app.UseWebSockets();

        app.Use(async (context, next) =>
        {
            if (RefuseConnections)
                context.Response.StatusCode = 403;
            else
                await next(context);
        });
        app.MapGet("/api/capabilities", () => Api1Only
            ? Results.NotFound()
            : Results.Json(new JsonObject
            {
                ["api"] = new JsonObject { ["versions"] = new JsonArray("v1", "v2") },
                ["x-plane"] = new JsonObject { ["version"] = Version },
            }));
        app.MapGet("/api/v1/datarefs/count", () => Results.Json(new { data = _byName.Count }));
        app.MapGet("/api/v2/datarefs", (HttpRequest r) =>
            Results.Json(new { data = Filter(r, _byName.Keys).Select(n => _byName[n]).Select(d => new { id = d.Id, name = d.Name, value_type = d.Type, is_writable = true }) }));
        app.MapGet("/api/v2/datarefs/{id:long}/value", (long id) =>
        {
            lock (_gate)
                return _byId.TryGetValue(id, out var d) ? Results.Json(new { data = d.Value }) : Error(404, "invalid_dataref_id");
        });
        app.MapMethods("/api/v2/datarefs/{id:long}/value", ["PATCH"], async (long id, HttpRequest r) =>
        {
            var body = await JsonNode.ParseAsync(r.Body);
            lock (_gate)
            {
                if (!_byId.TryGetValue(id, out var d))
                    return Error(404, "invalid_dataref_id");
                Write(d, body!["data"]!.GetValue<double>());
            }
            return Results.Ok();
        });
        app.MapGet("/api/v2/commands", (HttpRequest r) =>
            Results.Json(new { data = Filter(r, _commands.Keys).Select(n => new { id = _commands[n], name = n, description = n }) }));
        app.MapPost("/api/v2/command/{id:long}/activate", (long id) =>
        {
            lock (_gate)
                return RunCommand(id) ? Results.Ok() : Error(404, "invalid_command_id");
        });
        app.Map("/api/v2", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await ServeAsync(new Client(socket), context.RequestAborted);
        });

        await app.StartAsync();
        Port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
        _app = app;
        _pushCts = new CancellationTokenSource();
        _pushLoop = PushLoopAsync(_pushCts.Token);
    }

    /// <summary>Stops listening and drops clients, like quitting X-Plane. Can be started again on the same port.</summary>
    public async Task StopAsync()
    {
        if (_app is not { } app)
            return;
        _app = null;
        await _pushCts!.CancelAsync();
        try { await _pushLoop!; } catch (OperationCanceledException) { }
        foreach (var client in _clients.Keys)
        {
            try { await client.Socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "X-Plane quit", CancellationToken.None); }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException) { }
        }
        await app.StopAsync();
        await app.DisposeAsync();
        _clients.Clear();
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private static IEnumerable<string> Filter(HttpRequest request, IEnumerable<string> all)
    {
        var names = request.Query["filter[name]"];
        return names.Count == 0 ? all : names.Where(n => n is not null && all.Contains(n)).Select(n => n!);
    }

    private static IResult Error(int status, string code) =>
        Results.Json(new { error_code = code, error_message = code }, statusCode: status);

    private void Write(Dataref d, double value)
    {
        string name = d.Name;
        if (name.EndsWith("_833", StringComparison.Ordinal))
        {
            SetCom(name, (int)Math.Round(value));
            return;
        }
        if (name.Contains("/com", StringComparison.Ordinal) && name.EndsWith("frequency_hz", StringComparison.Ordinal))
        {
            SetCom(name + "_833", (int)Math.Round(value) * 10);
            return;
        }
        if (name.Contains("/nav", StringComparison.Ordinal) && name.EndsWith("frequency_hz", StringComparison.Ordinal))
            value = Math.Clamp(Math.Round(value / 5) * 5, 10_800, 11_795);
        else if (d.Type == "int")
            value = Math.Round(value);
        d.Value = value;
    }

    private void SetCom(string name833, int khz)
    {
        if (Com833Capable)
        {
            khz = (int)Math.Round(khz / 5.0) * 5;
            if (khz % 25 == 20)
                khz += 5;
            khz = Math.Clamp(khz, 118_000, 136_990);
        }
        else
        {
            khz = Math.Clamp((int)Math.Round(khz / 25.0) * 25, 118_000, 136_975);
        }
        _byName[name833].Value = khz;
        _byName[name833[..^4]].Value = khz / 10;
    }

    private bool RunCommand(long id)
    {
        var name = _commands.FirstOrDefault(c => c.Value == id).Key;
        if (name is null)
            return false;
        if (name.EndsWith("_standy_flip", StringComparison.Ordinal))
        {
            string radio = name["sim/radios/".Length..^"_standy_flip".Length]; // com1, nav2, ...
            string suffix = radio.StartsWith("com", StringComparison.Ordinal) ? "_833" : "";
            var active = _byName[$"{Act}{radio}_frequency_hz{suffix}"];
            var standby = _byName[$"{Act}{radio}_standby_frequency_hz{suffix}"];
            (active.Value, standby.Value) = (standby.Value, active.Value);
            if (suffix.Length > 0)
            {
                _byName[$"{Act}{radio}_frequency_hz"].Value = (int)active.Value / 10;
                _byName[$"{Act}{radio}_standby_frequency_hz"].Value = (int)standby.Value / 10;
            }
        }
        else if (name is "sim/instruments/barometer_2992" or "sim/instruments/barometer_std")
        {
            _byName["sim/cockpit2/gauges/actuators/barometer_setting_in_hg_pilot"].Value = 29.92;
        }
        return true;
    }

    private async Task ServeAsync(Client client, CancellationToken ct)
    {
        _clients[client] = 0;
        try
        {
            var buffer = new byte[64 * 1024];
            using var message = new MemoryStream();
            while (client.Socket.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await client.Socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                using var doc = JsonDocument.Parse(message.ToArray());
                var root = doc.RootElement.Clone();
                await _received.Writer.WriteAsync(root, ct);
                await client.SendAsync(Handle(client, root), ct);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            _clients.TryRemove(client, out _);
        }
    }

    private JsonObject Handle(Client client, JsonElement request)
    {
        var reqId = request.TryGetProperty("req_id", out var r) ? r.GetInt64() : 0;
        JsonObject Result(string? error) => error is null
            ? new JsonObject { ["req_id"] = reqId, ["type"] = "result", ["success"] = true }
            : new JsonObject { ["req_id"] = reqId, ["type"] = "result", ["success"] = false, ["error_code"] = error, ["error_message"] = error };

        var p = request.GetProperty("params");
        lock (_gate)
        {
            switch (request.GetProperty("type").GetString())
            {
                case "dataref_subscribe_values":
                    foreach (var item in p.GetProperty("datarefs").EnumerateArray())
                    {
                        long id = item.GetProperty("id").GetInt64();
                        if (!_byId.ContainsKey(id))
                            return Result("invalid_dataref_id");
                        client.Subscribed.Add(id);
                    }
                    return Result(null);

                case "dataref_set_values":
                    foreach (var item in p.GetProperty("datarefs").EnumerateArray())
                    {
                        if (!_byId.TryGetValue(item.GetProperty("id").GetInt64(), out var d))
                            return Result("invalid_dataref_id");
                        var value = item.GetProperty("value");
                        if (value.ValueKind != JsonValueKind.Number || (d.Type == "int" && !value.TryGetInt64(out _)))
                            return Result("incompatible_data");
                        Write(d, value.GetDouble());
                    }
                    return Result(null);

                case "command_set_is_active":
                    foreach (var item in p.GetProperty("commands").EnumerateArray())
                    {
                        if (item.GetProperty("is_active").GetBoolean() && !RunCommand(item.GetProperty("id").GetInt64()))
                            return Result("invalid_command_id");
                    }
                    return Result(null);

                default:
                    return Result("unknown_type");
            }
        }
    }

    private async Task PushLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await timer.WaitForNextTickAsync(ct))
        {
            foreach (var client in _clients.Keys)
            {
                var changed = new JsonObject();
                lock (_gate)
                {
                    foreach (long id in client.Subscribed)
                    {
                        double value = _byId[id].Value;
                        if (!client.LastSent.TryGetValue(id, out double sent) || sent != value)
                        {
                            changed[id.ToString()] = _byId[id].Type == "int" ? JsonValue.Create((long)value) : JsonValue.Create(value);
                            client.LastSent[id] = value;
                        }
                    }
                }
                if (changed.Count > 0)
                {
                    try { await client.SendAsync(new JsonObject { ["type"] = "dataref_update_values", ["data"] = changed }, ct); }
                    catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException) { }
                }
            }
        }
    }

    private sealed class Dataref(long id, string name, string type, double value)
    {
        public long Id { get; } = id;
        public string Name { get; } = name;
        public string Type { get; } = type;
        public double Value { get; set; } = value;
    }

    private sealed class Client(WebSocket socket)
    {
        private readonly SemaphoreSlim _send = new(1, 1);

        public WebSocket Socket { get; } = socket;
        public HashSet<long> Subscribed { get; } = [];
        public Dictionary<long, double> LastSent { get; } = new();

        public async Task SendAsync(JsonObject message, CancellationToken ct)
        {
            await _send.WaitAsync(ct);
            try { await Socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, ct); }
            finally { _send.Release(); }
        }
    }
}
