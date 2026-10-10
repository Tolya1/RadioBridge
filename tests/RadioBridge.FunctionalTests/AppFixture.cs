using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RadioBridge.Core.Automation;
using RadioBridge.Core.Settings;
using RadioBridge.FakeXPlane;

namespace RadioBridge.FunctionalTests;

/// <summary>Which simulator the suite runs against (environment variable RADIOBRIDGE_FT_SIM).</summary>
public enum SimMode
{
    /// <summary>The bundled X-Plane emulator (default; needs nothing installed).</summary>
    Emulator,

    /// <summary>A running X-Plane 12 with a flight loaded (Web API on port 8086).</summary>
    XPlane,

    /// <summary>A running MSFS 2020/2024 with a flight loaded (Windows only).</summary>
    Msfs,
}

/// <summary>
/// Starts the real RadioBridge app (window minimized, or --headless) with the automation API and a
/// virtual panel, pointed at the chosen simulator. One app instance serves the whole suite.
/// </summary>
public sealed class AppFixture : IAsyncLifetime
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private Process? _app;
    private readonly StringBuilder _output = new();

    public SimMode Mode { get; } = Environment.GetEnvironmentVariable("RADIOBRIDGE_FT_SIM")?.ToLowerInvariant() switch
    {
        null or "" or "emulator" => SimMode.Emulator,
        "xplane" => SimMode.XPlane,
        "msfs" => SimMode.Msfs,
        var other => throw new InvalidOperationException($"RADIOBRIDGE_FT_SIM must be emulator, xplane or msfs (got '{other}')."),
    };

    /// <summary>The emulator, in <see cref="SimMode.Emulator"/> mode.</summary>
    public XPlaneEmulator? Emulator { get; private set; }

    /// <summary>Direct access to X-Plane (emulated or real) for setting up and checking state; null for MSFS.</summary>
    public XPlaneRest? XPlane { get; private set; }

    public AppClient App { get; private set; } = null!;

    public BridgeSettings InitialSettings { get; private set; } = new();

    public string RunDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "runs", DateTime.Now.ToString("yyyyMMdd-HHmmss"));

    public string ExpectedSimName => Mode == SimMode.Msfs ? "MSFS" : "X-Plane";

    public async Task InitializeAsync()
    {
        int xplanePort = 8086;
        if (Mode == SimMode.Emulator)
        {
            Emulator = new XPlaneEmulator();
            await Emulator.StartAsync();
            xplanePort = Emulator.Port;
        }
        if (Mode != SimMode.Msfs)
            XPlane = new XPlaneRest(xplanePort);

        InitialSettings = new BridgeSettings
        {
            MsfsEnabled = Mode == SimMode.Msfs,
            XPlaneEnabled = Mode != SimMode.Msfs,
            XPlanePort = xplanePort,
        };
        Directory.CreateDirectory(RunDirectory);
        await File.WriteAllTextAsync(Path.Combine(RunDirectory, "settings.json"), JsonSerializer.Serialize(InitialSettings, AutomationServer.Json));

        int automationPort = FreePort();
        StartApp(automationPort);
        App = new AppClient(new HttpClient { BaseAddress = new Uri($"http://localhost:{automationPort}/api/") });

        await WaitAsync(async () => await App.TryStateAsync() is not null, "the app's automation API");
        await WaitAsync(async () => (await App.TryStateAsync())?.Sim.Connected == true,
            Mode == SimMode.Emulator ? "the app to connect to the emulator" : $"the app to connect to {Mode} (is it running with a flight loaded?)");
    }

    public async Task DisposeAsync()
    {
        if (_app is { HasExited: false })
        {
            try { await App.ShutdownAsync(); } catch (HttpRequestException) { }
            if (!_app.WaitForExit(5000))
                _app.Kill(entireProcessTree: true);
        }
        await File.WriteAllTextAsync(Path.Combine(RunDirectory, "app-output.txt"), _output.ToString());
        if (Emulator is not null)
            await Emulator.DisposeAsync();
    }

    /// <summary>Puts the app's settings back to how the suite started.</summary>
    public Task ResetSettingsAsync() => App.PutSettingsAsync(InitialSettings);

    private void StartApp(int automationPort)
    {
        string dll = FindAppDll();
        // Headless by default: a visible window can be closed by someone at the desk, which quits the app
        // mid-run. RADIOBRIDGE_FT_UI=window runs the same suite with the (minimized) window.
        bool headless = Environment.GetEnvironmentVariable("RADIOBRIDGE_FT_UI") != "window" || !OperatingSystem.IsWindows();
        var start = new ProcessStartInfo("dotnet")
        {
            ArgumentList = { dll, "--no-panel", "--automation", automationPort.ToString(), "--data-dir", RunDirectory, headless ? "--headless" : "--minimized" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        _app = Process.Start(start) ?? throw new InvalidOperationException("Could not start the app.");
        _app.OutputDataReceived += (_, e) => { lock (_output) _output.AppendLine(e.Data); };
        _app.ErrorDataReceived += (_, e) => { lock (_output) _output.AppendLine(e.Data); };
        _app.BeginOutputReadLine();
        _app.BeginErrorReadLine();
    }

    private async Task WaitAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + StartTimeout;
        while (!await condition())
        {
            if (_app is { HasExited: true })
                throw new InvalidOperationException($"The app exited with code {_app.ExitCode} while waiting for {what}.\n{_output}");
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for {what}. App output and logs: {RunDirectory}\n{_output}");
            await Task.Delay(200);
        }
    }

    /// <summary>The app built from this checkout, in the same configuration as the tests.</summary>
    private static string FindAppDll()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string configuration = dir.FullName.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RadioBridge.slnx")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException("Could not find the repository root (RadioBridge.slnx).");
        string dll = Path.Combine(dir.FullName, "src", "RadioBridge.Desktop", "bin", configuration, "net10.0", "RadioBridge.dll");
        return File.Exists(dll) ? dll : throw new FileNotFoundException("Build the app first (mise run build).", dll);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

[CollectionDefinition("app")]
public sealed class AppCollection : ICollectionFixture<AppFixture>;

/// <summary>X-Plane's REST API, used by the tests to set up and inspect the simulator directly.</summary>
public sealed class XPlaneRest(int port)
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri($"http://127.0.0.1:{port}/api/v2/") };
    private readonly Dictionary<string, long> _ids = new();

    public async Task SetAsync(string dataref, double value)
    {
        long id = await IdAsync(dataref);
        object data = value == Math.Round(value) && Math.Abs(value) < int.MaxValue ? (object)(long)value : value;
        using var response = await _http.PatchAsJsonAsync($"datarefs/{id}/value", new { data });
        response.EnsureSuccessStatusCode();
    }

    public async Task<double> GetAsync(string dataref)
    {
        long id = await IdAsync(dataref);
        var body = await _http.GetFromJsonAsync<JsonElement>($"datarefs/{id}/value");
        return body.GetProperty("data").GetDouble();
    }

    private async Task<long> IdAsync(string dataref)
    {
        if (_ids.TryGetValue(dataref, out long id))
            return id;
        var body = await _http.GetFromJsonAsync<JsonElement>($"datarefs?filter%5Bname%5D={Uri.EscapeDataString(dataref)}");
        id = body.GetProperty("data")[0].GetProperty("id").GetInt64();
        _ids[dataref] = id;
        return id;
    }
}
