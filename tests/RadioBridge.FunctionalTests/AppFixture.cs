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
        // Headless by default: a visible window can be closed by someone at the desk, which quits the app
        // mid-run. RADIOBRIDGE_FT_UI=window runs the same suite with the (minimized) window.
        bool headless = Environment.GetEnvironmentVariable("RADIOBRIDGE_FT_UI") != "window" || !OperatingSystem.IsWindows();
        var start = CreateStartInfo("--no-panel", "--automation", automationPort.ToString(), "--data-dir", RunDirectory, headless ? "--headless" : "--minimized");
        _app = Process.Start(start) ?? throw new InvalidOperationException("Could not start the app.");
        _app.OutputDataReceived += (_, e) => { lock (_output) _output.AppendLine(e.Data); };
        _app.ErrorDataReceived += (_, e) => { lock (_output) _output.AppendLine(e.Data); };
        _app.BeginOutputReadLine();
        _app.BeginErrorReadLine();
    }

    /// <summary>How to start the app under test (the packaged executable, or the build output) with these arguments.</summary>
    private ProcessStartInfo CreateStartInfo(params string[] arguments)
    {
        var start = FindPackagedApp() is { } packaged
            ? new ProcessStartInfo(packaged)
            : new ProcessStartInfo("dotnet") { ArgumentList = { FindAppDll() } };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardOutputEncoding = new System.Text.UTF8Encoding(false);
        start.UseShellExecute = false;
        AppPath = start.FileName == "dotnet" ? start.ArgumentList[0] : start.FileName;
        return start;
    }

    /// <summary>Runs the app with these arguments until it exits, e.g. for a command line option that just prints something.</summary>
    public async Task<(int ExitCode, string Output)> RunToExitAsync(params string[] arguments)
    {
        using var process = Process.Start(CreateStartInfo(arguments)) ?? throw new InvalidOperationException("Could not start the app.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{AppPath} {string.Join(' ', arguments)} did not exit.");
        }
        return (process.ExitCode, await output + await error);
    }

    private async Task WaitAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + StartTimeout;
        while (!await condition())
        {
            if (_app is { HasExited: true })
                throw new InvalidOperationException($"The app exited with code {_app.ExitCode} while waiting for {what}.\n{_output}");
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for {what} from {AppPath}. App output and logs: {RunDirectory}\n{_output}");
            await Task.Delay(200);
        }
    }

    /// <summary>The executable or DLL under test, for diagnostics.</summary>
    public string AppPath { get; private set; } = "";

    /// <summary>
    /// A packaged single-file app named by RADIOBRIDGE_APP (the file, or a folder containing it;
    /// relative paths are taken from the repository root), or null to test the regular build.
    /// </summary>
    private static string? FindPackagedApp()
    {
        if (Environment.GetEnvironmentVariable("RADIOBRIDGE_APP") is not { Length: > 0 } setting)
            return null;
        string path = Path.IsPathRooted(setting) ? setting : Path.Combine(RepositoryRoot(), setting);
        if (Directory.Exists(path))
            path = Path.Combine(path, OperatingSystem.IsWindows() ? "RadioBridge.exe" : "RadioBridge");
        return File.Exists(path) ? path : throw new FileNotFoundException("RADIOBRIDGE_APP doesn't point at a packaged RadioBridge (mise run functional:package).", path);
    }

    /// <summary>The app built from this checkout, in the same configuration as the tests.</summary>
    private static string FindAppDll()
    {
        string configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        string dll = Path.Combine(RepositoryRoot(), "src", "RadioBridge.Desktop", "bin", configuration, "net10.0", "RadioBridge.dll");
        return File.Exists(dll) ? dll : throw new FileNotFoundException("Build the app first (mise run build).", dll);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RadioBridge.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root (RadioBridge.slnx).");
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
