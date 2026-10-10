using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RadioBridge.Core.Sim.XPlane;

/// <summary>
/// X-Plane 12 through its built-in Web API on localhost (API v2, X-Plane 12.1.4 or newer), so no
/// plugin is needed. Dataref and command ids only live as long as the X-Plane session, so each
/// connection looks them up by name over REST and then subscribes over the WebSocket; X-Plane pushes
/// changed values about ten times a second.
/// </summary>
public sealed class XPlaneBackend : ISimBackend
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan UnsupportedRetryDelay = TimeSpan.FromSeconds(30);

    private readonly Func<int> _port;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private volatile Session? _session;
    private volatile RadioState _state = RadioState.Empty;
    private int _requestId;
    private bool _warnedForbidden;
    private bool _warnedUnsupported;

    /// <param name="port">Web API port, read on every connection attempt (X-Plane's default is 8086).</param>
    public XPlaneBackend(Func<int> port, ILogger<XPlaneBackend>? log = null)
    {
        _port = port;
        _log = log ?? NullLogger<XPlaneBackend>.Instance;
    }

    public string Name => "X-Plane";
    public bool IsConnected => _session is not null;
    public RadioState State => _state;

    public event Action? Changed;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    public void Send(SimCommand command)
    {
        var session = _session;
        if (session is null)
            return;

        switch (command)
        {
            case SetStandby c:
                SetDataref(session, XPlaneNames.Standby(c.Radio), w => w.WriteNumber("value", XPlaneNames.FrequencyRaw(c.Radio, c.Khz)));
                break;
            case SwapRadio c:
                RunCommand(session, XPlaneNames.Flip(c.Radio));
                break;
            case SetAdf c:
                SetDataref(session, XPlaneNames.Adf(c.Index), w => w.WriteNumber("value", c.Khz));
                break;
            case SetSquawk c:
                SetDataref(session, XPlaneNames.Squawk, w => w.WriteNumber("value", c.Code));
                break;
            case SetBaro c:
                SetDataref(session, XPlaneNames.Baro, w => w.WriteNumber("value", Math.Round(XPlaneNames.BaroRaw(c.Hpa), 3)));
                break;
            case SetBaroStandard:
                RunCommand(session, XPlaneNames.BaroStandard);
                break;
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TimeSpan delay = RetryDelay;
            try
            {
                delay = await RunSessionAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
            {
                if (!_warnedForbidden)
                    _log.LogWarning("X-Plane refused the Web API request. Turn off 'Disable incoming traffic' in X-Plane's Settings > Network.");
                _warnedForbidden = true;
            }
            catch (Exception ex) when (ex is HttpRequestException or WebSocketException or TaskCanceledException or IOException)
            {
                // Usually just "X-Plane is not running".
                _log.LogTrace("X-Plane not reachable: {Message}", ex.Message);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "X-Plane connection failed.");
            }

            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Runs one connection until it drops; returns how long to wait before the next attempt.</summary>
    private async Task<TimeSpan> RunSessionAsync(CancellationToken ct)
    {
        int port = _port();
        if (await FindLoopbackHostAsync(port, ct) is not { } host)
            return RetryDelay; // X-Plane is not running

        using var http = new HttpClient { BaseAddress = new Uri($"http://{host}:{port}/api/"), Timeout = TimeSpan.FromSeconds(3) };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var (supportsV2, version) = await GetCapabilitiesAsync(http, ct);
        if (!supportsV2)
        {
            if (!_warnedUnsupported)
                _log.LogWarning("X-Plane {Version} has no Web API v2; X-Plane 12.1.4 or newer is required.", version);
            _warnedUnsupported = true;
            return UnsupportedRetryDelay;
        }

        var datarefs = await LookupIdsAsync(http, "v2/datarefs", XPlaneNames.Datarefs, ct);
        var commands = await LookupIdsAsync(http, "v2/commands", XPlaneNames.Commands, ct);

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{host}:{port}/api/v2"), ct);

        var session = new Session(socket, datarefs, commands);
        session.Outbox.Writer.TryWrite(BuildMessage("dataref_subscribe_values", "datarefs", w =>
        {
            foreach (long id in datarefs.Values)
            {
                w.WriteStartObject();
                w.WriteNumber("id", id);
                w.WriteEndObject();
            }
        }));

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sender = SendLoopAsync(session, sessionCts.Token);
        _session = session;
        _log.LogInformation("Connected to X-Plane {Version} (Web API port {Port}).", version, port);
        Changed?.Invoke();

        try
        {
            await ReceiveLoopAsync(session, ct);
            _log.LogInformation("X-Plane closed the connection.");
        }
        finally
        {
            _session = null;
            _state = RadioState.Empty;
            await sessionCts.CancelAsync();
            try { await sender; } catch (Exception) { /* the socket is gone anyway */ }
            Changed?.Invoke();
        }

        return RetryDelay;
    }

    /// <summary>
    /// Picks the loopback address X-Plane listens on. Going through "localhost" would try ::1 first,
    /// and on Windows a refused loopback connection takes about two seconds.
    /// </summary>
    private static async Task<string?> FindLoopbackHostAsync(int port, CancellationToken ct)
    {
        foreach (var (address, host) in new[] { (IPAddress.Loopback, "127.0.0.1"), (IPAddress.IPv6Loopback, "[::1]") })
        {
            using var probe = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                await probe.ConnectAsync(address, port, timeout.Token);
                return host;
            }
            catch (SocketException)
            {
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
        }
        return null;
    }

    private static async Task<(bool SupportsV2, string Version)> GetCapabilitiesAsync(HttpClient http, CancellationToken ct)
    {
        using var response = await http.GetAsync("capabilities", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return (false, "12.1.1-12.1.3"); // the capabilities endpoint arrived together with v2
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = doc.RootElement;
        bool v2 = root.TryGetProperty("api", out var api)
                  && api.TryGetProperty("versions", out var versions)
                  && versions.EnumerateArray().Any(v => v.GetString() == "v2");
        string version = root.TryGetProperty("x-plane", out var xp) && xp.TryGetProperty("version", out var ver)
            ? ver.GetString() ?? "?"
            : "?";
        return (v2, version);
    }

    private async Task<Dictionary<string, long>> LookupIdsAsync(HttpClient http, string path, IEnumerable<string> names, CancellationToken ct)
    {
        var wanted = names.ToList();
        string query = string.Join("&", wanted.Select(n => "filter%5Bname%5D=" + Uri.EscapeDataString(n)));
        using var response = await http.GetAsync($"{path}?{query}", ct);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var ids = new Dictionary<string, long>();
        foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
            ids[item.GetProperty("name").GetString()!] = item.GetProperty("id").GetInt64();

        var missing = wanted.Where(n => !ids.ContainsKey(n)).ToList();
        if (missing.Count > 0)
            _log.LogWarning("X-Plane does not know {Kind}: {Names}", path, string.Join(", ", missing));
        return ids;
    }

    private static async Task SendLoopAsync(Session session, CancellationToken ct)
    {
        await foreach (var message in session.Outbox.Reader.ReadAllAsync(ct))
            await session.Socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private async Task ReceiveLoopAsync(Session session, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (session.Socket.State == WebSocketState.Open)
        {
            message.SetLength(0);
            ValueWebSocketReceiveResult result;
            do
            {
                result = await session.Socket.ReceiveAsync(buffer.AsMemory(), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            HandleMessage(session, message.GetBuffer().AsMemory(0, (int)message.Length));
        }
    }

    private void HandleMessage(Session session, ReadOnlyMemory<byte> json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        switch (root.TryGetProperty("type", out var type) ? type.GetString() : null)
        {
            case "dataref_update_values":
                var state = _state;
                foreach (var value in root.GetProperty("data").EnumerateObject())
                {
                    if (value.Value.ValueKind == JsonValueKind.Number
                        && long.TryParse(value.Name, out long id)
                        && session.DatarefNames.TryGetValue(id, out var name))
                    {
                        state = XPlaneNames.Apply(state, name, value.Value.GetDouble());
                    }
                }
                _state = state;
                Changed?.Invoke();
                break;

            case "result" when root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False:
                _log.LogWarning("X-Plane rejected request {Id}: {Code} {Message}",
                    root.TryGetProperty("req_id", out var req) ? req.ToString() : "?",
                    root.TryGetProperty("error_code", out var code) ? code.GetString() : "?",
                    root.TryGetProperty("error_message", out var msg) ? msg.GetString() : "");
                break;
        }
    }

    private void SetDataref(Session session, string name, Action<Utf8JsonWriter> writeValue)
    {
        if (!session.DatarefIds.TryGetValue(name, out long id))
            return;
        session.Outbox.Writer.TryWrite(BuildMessage("dataref_set_values", "datarefs", w =>
        {
            w.WriteStartObject();
            w.WriteNumber("id", id);
            writeValue(w);
            w.WriteEndObject();
        }));
    }

    private void RunCommand(Session session, string name)
    {
        if (!session.CommandIds.TryGetValue(name, out long id))
            return;
        session.Outbox.Writer.TryWrite(BuildMessage("command_set_is_active", "commands", w =>
        {
            w.WriteStartObject();
            w.WriteNumber("id", id);
            w.WriteBoolean("is_active", true);
            w.WriteNumber("duration", 0); // press and release
            w.WriteEndObject();
        }));
    }

    private byte[] BuildMessage(string type, string listName, Action<Utf8JsonWriter> writeItems)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("req_id", Interlocked.Increment(ref _requestId));
            w.WriteString("type", type);
            w.WriteStartObject("params");
            w.WriteStartArray(listName);
            writeItems(w);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts.Dispose();
    }

    private sealed class Session(ClientWebSocket socket, Dictionary<string, long> datarefIds, Dictionary<string, long> commandIds)
    {
        public ClientWebSocket Socket { get; } = socket;
        public IReadOnlyDictionary<string, long> DatarefIds { get; } = datarefIds;
        public IReadOnlyDictionary<long, string> DatarefNames { get; } = datarefIds.ToDictionary(p => p.Value, p => p.Key);
        public IReadOnlyDictionary<string, long> CommandIds { get; } = commandIds;
        public Channel<byte[]> Outbox { get; } = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    }
}
