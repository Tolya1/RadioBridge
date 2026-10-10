using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RadioBridge.Core.Panel;
using RadioBridge.Core.Settings;
using RadioBridge.Core.Sim;

namespace RadioBridge.Core.Automation;

/// <summary>
/// Opt-in local HTTP API for functional tests and tooling (started with <c>--automation PORT</c>).
/// Listens on localhost only.
/// <list type="bullet">
/// <item><c>GET /api/state</c>: panel view, display text, the raw display report and the sim's own values.</item>
/// <item><c>POST /api/panel/report</c> <c>{"bits": n}</c>: injects a 24-bit input report into the virtual panel.</item>
/// <item><c>GET|PUT /api/settings</c>: reads or replaces the settings.</item>
/// <item><c>POST /api/shutdown</c>: exits the app.</item>
/// </list>
/// </summary>
public sealed class AutomationServer : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        WriteIndented = true,
    };

    private readonly int _port;
    private readonly PanelController _controller;
    private readonly VirtualRadioPanel _panel;
    private readonly ISimBackend _sim;
    private readonly SettingsStore _settings;
    private readonly Action _shutdown;
    private readonly ILogger _log;
    private readonly HttpListener _listener = new();
    private Task? _loop;

    public AutomationServer(int port, PanelController controller, VirtualRadioPanel panel, ISimBackend sim,
        SettingsStore settings, Action shutdown, ILogger<AutomationServer>? log = null)
    {
        _port = port;
        _controller = controller;
        _panel = panel;
        _sim = sim;
        _settings = settings;
        _shutdown = shutdown;
        _log = log ?? NullLogger<AutomationServer>.Instance;
    }

    public void Start()
    {
        _listener.Prefixes.Add($"http://localhost:{_port}/");
        _listener.Start();
        _log.LogWarning("Automation API listening on http://localhost:{Port}/api/ (testing only).", _port);
        _loop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (!_listener.IsListening)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            object? result = (request.HttpMethod, request.Url?.AbsolutePath.TrimEnd('/')) switch
            {
                ("GET", "/api/state") => Snapshot(),
                ("POST", "/api/panel/report") => Inject(await ReadAsync<ReportRequest>(request)),
                ("GET", "/api/settings") => _settings.Current,
                ("PUT", "/api/settings") => ReplaceSettings(await ReadAsync<BridgeSettings>(request)),
                ("POST", "/api/shutdown") => Shutdown(),
                _ => NotFound(response),
            };
            await WriteAsync(response, result);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidDataException)
        {
            response.StatusCode = 400;
            await WriteAsync(response, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Automation request {Method} {Url} failed.", request.HttpMethod, request.Url);
            response.StatusCode = 500;
            await WriteAsync(response, new { error = ex.Message });
        }
    }

    private object Snapshot()
    {
        var view = _controller.View;
        return new
        {
            panel = new { connected = view.PanelConnected, upper = view.Upper, lower = view.Lower },
            displays = view.Displays,
            displayReport = Convert.ToHexString(_panel.DisplayReport),
            sim = new { connected = _sim.IsConnected, name = _sim.Name, state = _sim.State },
            settings = _settings.Current,
        };
    }

    private object Inject(ReportRequest? body)
    {
        if (body?.Bits is not int bits || bits is < 0 or > 0xFFFFFF)
            throw new ArgumentException("Expected {\"bits\": 0..16777215}.");
        var events = _panel.Inject([(byte)bits, (byte)(bits >> 8), (byte)(bits >> 16)]);
        return new { events = events.Select(e => e.ToString()) };
    }

    private object ReplaceSettings(BridgeSettings? settings)
    {
        if (settings is null)
            throw new ArgumentException("Expected a settings object.");
        _settings.Update(_ => settings);
        return _settings.Current;
    }

    private object Shutdown()
    {
        _ = Task.Delay(100).ContinueWith(_ => _shutdown());
        return new { shuttingDown = true };
    }

    private static object NotFound(HttpListenerResponse response)
    {
        response.StatusCode = 404;
        return new { error = "Unknown endpoint." };
    }

    private static async Task<T?> ReadAsync<T>(HttpListenerRequest request) =>
        await JsonSerializer.DeserializeAsync<T>(request.InputStream, Json);

    private static async Task WriteAsync(HttpListenerResponse response, object? body)
    {
        response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(response.OutputStream, body, Json);
        response.Close();
    }

    public void Dispose()
    {
        if (_listener.IsListening)
            _listener.Stop();
        _listener.Close();
        try { _loop?.Wait(TimeSpan.FromSeconds(1)); } catch (AggregateException) { }
    }

    private sealed record ReportRequest(int? Bits);
}
