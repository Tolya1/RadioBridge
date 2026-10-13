using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RadioBridge.Core;
using RadioBridge.Core.Automation;
using RadioBridge.Core.Settings;

namespace RadioBridge.FunctionalTests;

/// <summary>
/// Launching RadioBridge while another runs, with real processes: a newer launch replaces an older one, an
/// older or equal launch leaves the running one alone. The copies run under a test instance name of their own
/// (RADIOBRIDGE_TEST_INSTANCE), so they never meet the RadioBridge the user is running, and without the panel or
/// any simulator.
/// </summary>
[Collection("app")]
public sealed class InstanceHandOverTests(AppFixture fx) : IDisposable
{
    private readonly string _instance = Guid.NewGuid().ToString("N");
    private readonly List<Process> _started = [];

    public void Dispose()
    {
        foreach (var process in _started)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.Dispose();
        }
    }

    /// <summary>The log of the copy launched as <paramref name="version"/> (the window app logs to its file only).</summary>
    private string Log(string version)
    {
        string logs = Path.Combine(_data[version], "logs");
        return string.Concat(Directory.GetFiles(logs, "*.log").Select(path =>
        {
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            return reader.ReadToEnd();
        }));
    }

    private readonly Dictionary<string, string> _data = [];

    private Process Launch(string version, StringBuilder output, string mode = "--headless")
    {
        // A data folder each: settings with no simulators, and a log file of its own.
        string data = _data[version] = Path.Combine(fx.RunDirectory, "handover", _instance[..8], version);
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "settings.json"),
            JsonSerializer.Serialize(new BridgeSettings { MsfsEnabled = false, XPlaneEnabled = false, ShowReaderAtStart = false }, AutomationServer.Json));
        var process = fx.StartAnother(
            new Dictionary<string, string> { [SingleInstance.TestInstanceVariable] = _instance, ["RADIOBRIDGE_TEST_VERSION"] = version },
            output, mode, "--no-panel", "--data-dir", data);
        _started.Add(process);
        return process;
    }

    /// <summary>Asks the running copy which version it is (as an older launch would, so it stays).</summary>
    private Version? RunningVersion() =>
        SingleInstance.AskRunning(new Version(0, 0, 1), show: false, pipeName: SingleInstance.PipeNameFor(_instance)).Running;

    private static async Task Until(Func<bool> condition, string what, int timeoutMs = 20_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(100);
        }
    }

    [SkippableTheory]
    [InlineData("--headless")]
    [InlineData("--minimized")] // the window app quits through Avalonia, then lets go of the instance in Main
    public async Task A_newer_launch_replaces_the_running_one_and_an_older_launch_leaves_it_running(string mode)
    {
        Skip.If(mode == "--minimized" && !OperatingSystem.IsWindows(), "The window variant runs on Windows (as the suite's window mode does).");
        var olderOutput = new StringBuilder();
        var older = Launch("0.1.0", olderOutput, mode);
        await Until(() => RunningVersion() == new Version(0, 1, 0), "0.1.0 to run");

        var newerOutput = new StringBuilder();
        var newer = Launch("0.2.0", newerOutput, mode);

        await Until(() => older.HasExited, $"0.1.0 to make way. Its output:\n{olderOutput}");
        Assert.Equal(0, older.ExitCode);
        await Until(() => RunningVersion() == new Version(0, 2, 0), $"0.2.0 to take over. Its output:\n{newerOutput}");
        Assert.Contains("Quitting: RadioBridge 0.2.0 is starting.", Log("0.1.0"));
        Assert.Contains("Took over from RadioBridge 0.1.0", Log("0.2.0"));

        var lateOutput = new StringBuilder();
        var late = Launch("0.1.9", lateOutput);
        await Until(() => late.HasExited, "the older launch to give up");
        Assert.Equal(1, late.ExitCode);
        Assert.Contains("RadioBridge is already running.", lateOutput.ToString());
        Assert.False(newer.HasExited);
        Assert.Equal(new Version(0, 2, 0), RunningVersion());
    }
}
