using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RadioBridge.Core.Diagnostics;

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

    /// <summary>
    /// How long a command (XFER) is held down. Like a quick click on the cockpit button: some aircraft act
    /// only when a command is released after being held for at least a frame (REP's Bendix King KX165 in
    /// the SF-260, for one), and ignore a press that starts and ends in the same frame. 50 ms was enough at
    /// a normal frame rate; this leaves room for a slow one.
    /// </summary>
    internal const double PressSeconds = 0.15;

    private const string AircraftPath = "sim/aircraft/view/acf_relative_path";

    private readonly Func<int> _port;
    private readonly ILogger _log;
    private readonly Recorder _recorder;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private volatile Session? _session;
    private volatile RadioState _state = RadioState.Empty;
    private int _requestId;
    private SimNotice? _notice;
    private bool _warnedForbidden;
    private bool _warnedUnsupported;

    /// <param name="port">Web API port, read on every connection attempt (X-Plane's default is 8086).</param>
    /// <param name="recorder">The debug recording; while it runs, this also watches the aircraft's radio datarefs.</param>
    public XPlaneBackend(Func<int> port, ILogger<XPlaneBackend>? log = null, Recorder? recorder = null)
    {
        _port = port;
        _log = log ?? NullLogger<XPlaneBackend>.Instance;
        _recorder = recorder ?? new Recorder();
        _recorder.Changed += OnRecordingChanged;
    }

    public string Name => "X-Plane";
    public bool IsConnected => _session is not null;

    /// <summary>X-Plane's values, with what RadioBridge set and X-Plane hasn't answered yet laid over them (<see cref="XPlaneRequests"/>).</summary>
    public RadioState State => _session is { } session ? session.Requests.Apply(_state) : _state;

    /// <summary>
    /// Set only for problems that are certainly X-Plane's: a refusal, a version that is too old, or answering so
    /// late that changes reach the cockpit seconds after they were made.
    /// </summary>
    public SimNotice? Notice => _session is { } session && session.Requests.SlowSeconds() is double late
        ? SlowNotice(late)
        : Volatile.Read(ref _notice);

    internal static SimNotice SlowNotice(double seconds) => new(
        $"X-Plane is answering slowly: changes reach the cockpit about {Math.Max(2, Math.Round(seconds)):0} s late. " +
        "The panel keeps showing what you set until X-Plane catches up.");

    public event Action? Changed;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    private void SetNotice(SimNotice? notice)
    {
        if (Interlocked.Exchange(ref _notice, notice) != notice)
            Changed?.Invoke();
    }

    public void Send(SimCommand command)
    {
        var session = _session;
        if (session is null)
            return;

        switch (command)
        {
            case SetStandby c:
                SetDataref(session, XPlaneNames.Standby(c.Radio), XPlaneNames.FrequencyRaw(c.Radio, c.Khz));
                break;
            case SwapRadio c:
                // Laid over X-Plane's values as the pair the swap should produce (not as "swap whatever is
                // there", which would undo the swap once X-Plane has made it).
                var pair = State.Get(c.Radio);
                var swapped = new RadioPair(pair.StandbyKhz, pair.ActiveKhz);
                RunCommand(session, XPlaneNames.Flip(c.Radio), s => s.With(c.Radio, swapped));
                break;
            case SetAdf c:
                SetDataref(session, XPlaneNames.Adf(c.Index), c.Khz);
                break;
            case SetSquawk c:
                SetDataref(session, XPlaneNames.Squawk, c.Code);
                break;
            case SetBaro c:
                SetDataref(session, XPlaneNames.Baro, XPlaneNames.BaroRaw(c.Hpa));
                break;
            case SetBaroStandard:
                SetDataref(session, XPlaneNames.Baro, XPlaneNames.StandardBaroRaw);
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
                SetNotice(new SimNotice("X-Plane refuses RadioBridge's connection. In X-Plane's Settings › Network, switch off 'Disable incoming traffic'."));
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
        {
            SetNotice(null); // X-Plane is not running, which is nothing to warn about
            return RetryDelay;
        }

        using var http = new HttpClient { BaseAddress = new Uri($"http://{host}:{port}/api/"), Timeout = TimeSpan.FromSeconds(3) };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var (api, version) = await GetCapabilitiesAsync(http, ct);
        if (api != WebApi.Ready)
        {
            if (!_warnedUnsupported && api == WebApi.OldXPlane)
                _log.LogWarning("X-Plane {Version} has no Web API v2; X-Plane 12.1.4 or newer is required.", version);
            else if (!_warnedUnsupported)
                _log.LogWarning("Something other than X-Plane's Web API answers on port {Port}. If X-Plane uses another port, set it in RadioBridge's settings.", port);
            _warnedUnsupported = true;

            // Only a certain X-Plane problem becomes a notice: another program on the port may simply be
            // unrelated to the simulator, so that case stays a log line.
            SetNotice(api == WebApi.OldXPlane
                ? new SimNotice($"X-Plane {version} is too old for RadioBridge, which needs X-Plane 12.1.4 or newer.")
                : null);
            return UnsupportedRetryDelay;
        }

        var datarefs = await LookupIdsAsync(http, "v2/datarefs", XPlaneNames.Datarefs, ct);
        var commands = await LookupIdsAsync(http, "v2/commands", XPlaneNames.Commands, ct);

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{host}:{port}/api/v2"), ct);

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var session = new Session(socket, http, datarefs, commands, sessionCts.Token);
        Subscribe(session, datarefs.Values);

        var sender = SendLoopAsync(session, sessionCts.Token);
        var watchdog = WatchAnswersAsync(session, sessionCts.Token);
        _session = session;
        _warnedForbidden = false; // warn again should it ever be refused again
        _warnedUnsupported = false;
        Volatile.Write(ref _notice, null); // announced by the Changed below
        _log.LogInformation("Connected to X-Plane {Version} (Web API port {Port}).", version, port);
        _recorder.Record("xplane", "connected", w =>
        {
            w.WriteString("version", version);
            w.WriteString("address", $"{host}:{port}");
            WriteIds(w, "datarefs", datarefs);
            WriteIds(w, "commands", commands);
        });
        if (_recorder.IsRecording)
            _ = UpdateWatchAsync(session);
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
            try { await watchdog; } catch (OperationCanceledException) { }
            _recorder.Record("xplane", "disconnected");
            Changed?.Invoke();
        }

        return RetryDelay;
    }

    private static void WriteIds(Utf8JsonWriter w, string name, IReadOnlyDictionary<string, long> ids)
    {
        w.WriteStartObject(name);
        foreach (var (key, id) in ids)
            w.WriteNumber(key, id);
        w.WriteEndObject();
    }

    private void Subscribe(Session session, IEnumerable<long> ids, bool subscribe = true)
    {
        var (message, request) = BuildMessage(subscribe ? "dataref_subscribe_values" : "dataref_unsubscribe_values", "datarefs", w =>
        {
            foreach (long id in ids)
            {
                w.WriteStartObject();
                w.WriteNumber("id", id);
                w.WriteEndObject();
            }
        });
        session.Requests.Sent(request); // before it can be answered
        session.Outbox.Writer.TryWrite(message);
    }

    /// <summary>
    /// Notices when X-Plane starts or stops answering late, so the log and the recording say when, and the
    /// window's notice appears without waiting for the next value.
    /// </summary>
    private async Task WatchAnswersAsync(Session session, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        bool slow = false;
        while (await timer.WaitForNextTickAsync(ct))
        {
            double? late = session.Requests.SlowSeconds();
            if ((late is not null) == slow)
                continue;
            slow = late is not null;
            if (slow)
                _log.LogWarning("X-Plane is answering {Seconds:0.0} s late.", late);
            else
                _log.LogInformation("X-Plane answers in time again.");
            _recorder.Record("xplane", slow ? "slow" : "caught-up", w =>
            {
                if (late is double seconds)
                    w.WriteNumber("seconds", Math.Round(seconds, 1));
            });
            Changed?.Invoke();
        }
    }

    private void OnRecordingChanged()
    {
        if (_session is { } session)
            _ = UpdateWatchAsync(session);
    }

    /// <summary>
    /// While recording, also subscribes to the aircraft's radio datarefs (<see cref="XPlaneWatchList"/>);
    /// otherwise drops them. <paramref name="rescan"/> looks for them again, after another aircraft was loaded.
    /// </summary>
    private async Task UpdateWatchAsync(Session session, bool rescan = false)
    {
        try
        {
            await session.WatchGate.WaitAsync(session.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            bool want = _recorder.IsRecording;
            if (!session.Watch.IsEmpty && (!want || rescan))
            {
                Subscribe(session, session.Watch.Keys.ToList(), subscribe: false);
                session.Watch.Clear();
            }
            if (!want || !session.Watch.IsEmpty)
                return;

            using var response = await session.Http.GetAsync("v2/datarefs", session.Token);
            response.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(session.Token), cancellationToken: session.Token);
            var all = doc.RootElement.GetProperty("data").EnumerateArray()
                .Select(d => (Id: d.GetProperty("id").GetInt64(), Name: d.GetProperty("name").GetString() ?? "",
                    Type: d.TryGetProperty("value_type", out var t) ? t.GetString() ?? "" : ""))
                .ToList();
            var wanted = XPlaneWatchList.Select(all.Select(d => (d.Name, d.Type)), session.DatarefIds.Keys).ToHashSet(StringComparer.Ordinal);
            foreach (var d in all.Where(d => wanted.Contains(d.Name)))
                session.Watch[d.Id] = new Watched(d.Name, d.Type);

            _recorder.Record("xplane", "watching", w =>
            {
                w.WriteNumber("count", session.Watch.Count);
                w.WriteNumber("of", all.Count);
                w.WriteStartObject("datarefs");
                foreach (var (id, watched) in session.Watch.OrderBy(p => p.Value.Name, StringComparer.Ordinal))
                    w.WriteString(watched.Name, watched.Type);
                w.WriteEndObject();
            });
            if (!session.Watch.IsEmpty)
                Subscribe(session, session.Watch.Keys.ToList());
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or OperationCanceledException or InvalidOperationException or KeyNotFoundException)
        {
            if (!session.Token.IsCancellationRequested)
                _log.LogDebug(ex, "Could not set up the recording's extra datarefs.");
        }
        finally
        {
            session.WatchGate.Release();
        }
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

    private enum WebApi { Ready, OldXPlane, NotXPlane }

    /// <summary>Tells X-Plane 12.1.4+ apart from older X-Plane and from other programs on the port.</summary>
    private static async Task<(WebApi Api, string Version)> GetCapabilitiesAsync(HttpClient http, CancellationToken ct)
    {
        using var response = await http.GetAsync("capabilities", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // The capabilities endpoint arrived with v2; X-Plane 12.1.1-12.1.3 only has the v1 API.
            using var v1 = await http.GetAsync("v1/datarefs/count", ct);
            return (v1.IsSuccessStatusCode ? WebApi.OldXPlane : WebApi.NotXPlane, "12.1.1-12.1.3");
        }
        response.EnsureSuccessStatusCode();

        try
        {
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = doc.RootElement;
            if (!root.TryGetProperty("x-plane", out var xp))
                return (WebApi.NotXPlane, "?");
            bool v2 = root.TryGetProperty("api", out var api)
                      && api.TryGetProperty("versions", out var versions)
                      && versions.EnumerateArray().Any(v => v.GetString() == "v2");
            string version = xp.TryGetProperty("version", out var ver) ? ver.GetString() ?? "?" : "?";
            return (v2 ? WebApi.Ready : WebApi.OldXPlane, version);
        }
        catch (JsonException)
        {
            return (WebApi.NotXPlane, "?");
        }
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
                var data = root.GetProperty("data");
                var state = _state;
                foreach (var value in data.EnumerateObject())
                {
                    if (value.Value.ValueKind == JsonValueKind.Number
                        && long.TryParse(value.Name, out long id)
                        && session.DatarefNames.TryGetValue(id, out var name))
                    {
                        state = XPlaneNames.Apply(state, name, value.Value.GetDouble());
                    }
                }
                _state = state;
                if (_recorder.IsRecording)
                    RecordValues(session, data);
                Changed?.Invoke();
                break;

            case "result":
                bool success = !root.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.False;
                string request = root.TryGetProperty("req_id", out var req) ? req.ToString() : "?";
                string? error = root.TryGetProperty("error_code", out var code) ? code.GetString() : null;
                string? message = root.TryGetProperty("error_message", out var msg) ? msg.GetString() : null;
                var lag = int.TryParse(request, out int requestId) ? session.Requests.Answered(requestId, success) : null;
                if (!success)
                    _log.LogWarning("X-Plane rejected request {Id}: {Code} {Message}", request, error ?? "?", message ?? "");
                _recorder.Record("xplane", "result", w =>
                {
                    w.WriteString("req", request);
                    w.WriteBoolean("success", success);
                    if (lag is { } took)
                        w.WriteNumber("lagMs", Math.Round(took.TotalMilliseconds, 1)); // from queueing the request to this answer
                    if (!success)
                    {
                        w.WriteString("error", error);
                        w.WriteString("message", message);
                    }
                });
                break;
        }
    }

    /// <summary>Records pushed values by dataref name; byte data (the aircraft's path and name) as text.</summary>
    private void RecordValues(Session session, JsonElement data)
    {
        string? aircraft = null;
        _recorder.Record("xplane", "values", w =>
        {
            w.WriteStartObject("values");
            foreach (var value in data.EnumerateObject())
            {
                long.TryParse(value.Name, out long id);
                if (session.DatarefNames.TryGetValue(id, out var name))
                {
                    w.WritePropertyName(name);
                    value.Value.WriteTo(w);
                }
                else if (session.Watch.TryGetValue(id, out var watched))
                {
                    w.WritePropertyName(watched.Name);
                    if (watched.Type == "data" && value.Value.ValueKind == JsonValueKind.String)
                    {
                        string text = DecodeData(value.Value.GetString());
                        w.WriteStringValue(text);
                        if (watched.Name == AircraftPath)
                            aircraft = text;
                    }
                    else
                    {
                        value.Value.WriteTo(w);
                    }
                }
                else
                {
                    w.WritePropertyName("id:" + value.Name);
                    value.Value.WriteTo(w);
                }
            }
            w.WriteEndObject();
        });

        // Another aircraft brings other add-on datarefs: look for them again.
        if (aircraft is not null)
        {
            string? before = Interlocked.Exchange(ref session.Aircraft, aircraft);
            if (before is not null && before != aircraft)
                _ = UpdateWatchAsync(session, rescan: true);
        }
    }

    private static string DecodeData(string? base64)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64 ?? "")).TrimEnd('\0');
        }
        catch (FormatException)
        {
            return base64 ?? "";
        }
    }

    /// <summary>
    /// Writes a dataref. Writes to one dataref closer together than <see cref="XPlaneRequests.CoalesceWindow"/>
    /// are merged: during a fast spin only the newest value goes out every 100 ms, not one per detent.
    /// </summary>
    private void SetDataref(Session session, string name, double value)
    {
        if (!session.DatarefIds.TryGetValue(name, out long id))
        {
            _log.LogWarning("Not writing {Name}: this X-Plane has no such dataref.", name);
            return;
        }
        Func<RadioState, RadioState> apply = s => XPlaneNames.Apply(s, name, value);
        lock (session.SendGate)
        {
            var later = session.Requests.PlanWrite(name, value, apply);
            if (later is null)
            {
                SendSet(session, name, id, value, apply);
                return;
            }
            if (later > TimeSpan.Zero)
                _ = SendHeldBackAsync(session, name, id, later.Value);
            _recorder.Record("xplane", "held", w =>
            {
                w.WriteString("dataref", name);
                w.WriteNumber("value", value);
            });
        }
    }

    /// <summary>Sends the newest held-back value of a dataref once the coalescing window is over.</summary>
    private async Task SendHeldBackAsync(Session session, string name, long id, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, session.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        lock (session.SendGate)
        {
            if (session.Requests.TakeQueued(name) is { } queued)
                SendSet(session, name, id, queued.Value, expectation: queued.Expectation);
        }
    }

    /// <summary>Sends one write (under <see cref="Session.SendGate"/>). Whole numbers go as integers (the int datarefs insist on it).</summary>
    private void SendSet(Session session, string name, long id, double value, Func<RadioState, RadioState>? apply = null,
        XPlaneRequests.Expectation? expectation = null)
    {
        _log.LogDebug("X-Plane <- {Name} = {Value}", name, value);
        var (message, request) = BuildMessage("dataref_set_values", "datarefs", w =>
        {
            w.WriteStartObject();
            w.WriteNumber("id", id);
            if (value == Math.Floor(value) && Math.Abs(value) < 1e15)
                w.WriteNumber("value", (long)value);
            else
                w.WriteNumber("value", value);
            w.WriteEndObject();
        });
        session.Requests.Sent(request, name, apply, expectation); // before it can be answered
        session.Outbox.Writer.TryWrite(message);
        _recorder.Record("xplane", "set", w =>
        {
            w.WriteNumber("req", request);
            w.WriteString("dataref", name);
            w.WriteNumber("value", value);
        });
    }

    /// <param name="apply">What the command does to the radios, laid over X-Plane's values until it is answered.</param>
    private void RunCommand(Session session, string name, Func<RadioState, RadioState>? apply = null)
    {
        if (!session.CommandIds.TryGetValue(name, out long id))
        {
            _log.LogWarning("Not running {Name}: this X-Plane has no such command.", name);
            return;
        }
        _log.LogDebug("X-Plane <- command {Name}", name);
        lock (session.SendGate)
        {
            var (message, request) = BuildMessage("command_set_is_active", "commands", w =>
            {
                w.WriteStartObject();
                w.WriteNumber("id", id);
                w.WriteBoolean("is_active", true);
                w.WriteNumber("duration", PressSeconds); // X-Plane releases it after this long
                w.WriteEndObject();
            });
            session.Requests.Sent(request, apply: apply);
            session.Outbox.Writer.TryWrite(message);
            _recorder.Record("xplane", "command", w =>
            {
                w.WriteNumber("req", request);
                w.WriteString("command", name);
                w.WriteNumber("pressSeconds", PressSeconds);
            });
        }
    }

    private (byte[] Message, int Request) BuildMessage(string type, string listName, Action<Utf8JsonWriter> writeItems)
    {
        var buffer = new ArrayBufferWriter<byte>();
        int id = Interlocked.Increment(ref _requestId);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("req_id", id);
            w.WriteString("type", type);
            w.WriteStartObject("params");
            w.WriteStartArray(listName);
            writeItems(w);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return (buffer.WrittenSpan.ToArray(), id);
    }

    public void Dispose()
    {
        _recorder.Changed -= OnRecordingChanged;
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts.Dispose();
    }

    private sealed record Watched(string Name, string Type);

    private sealed class Session(ClientWebSocket socket, HttpClient http, Dictionary<string, long> datarefIds,
        Dictionary<string, long> commandIds, CancellationToken token)
    {
        public ClientWebSocket Socket { get; } = socket;
        public HttpClient Http { get; } = http;
        public IReadOnlyDictionary<string, long> DatarefIds { get; } = datarefIds;
        public IReadOnlyDictionary<long, string> DatarefNames { get; } = datarefIds.ToDictionary(p => p.Value, p => p.Key);
        public IReadOnlyDictionary<string, long> CommandIds { get; } = commandIds;
        public Channel<byte[]> Outbox { get; } = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

        /// <summary>Requests in flight, coalesced writes, and values X-Plane hasn't answered yet.</summary>
        public XPlaneRequests Requests { get; } = new();

        /// <summary>Keeps writes and commands in the order they were decided, held-back writes included.</summary>
        public object SendGate { get; } = new();

        /// <summary>Ends with the connection.</summary>
        public CancellationToken Token { get; } = token;

        /// <summary>The extra datarefs a recording watches, by id.</summary>
        public ConcurrentDictionary<long, Watched> Watch { get; } = new();
        public SemaphoreSlim WatchGate { get; } = new(1, 1);

        /// <summary>The loaded aircraft's path, once a recording has seen it.</summary>
        public string? Aircraft;
    }
}
