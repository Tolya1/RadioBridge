using Microsoft.Extensions.Logging.Abstractions;
using RadioBridge.Core;
using RadioBridge.Core.Settings;

namespace RadioBridge.Tests;

public sealed class BridgeHostRecordingTests : IDisposable
{
    private const string NoSims = """{ "MsfsEnabled": false, "XPlaneEnabled": false }""";

    private readonly TempFolder _data = new();

    public void Dispose() => _data.Dispose();

    /// <summary>Stands in for the folder RadioBridge.exe was started from.</summary>
    private string ExeFolder => Path.Combine(_data.Path, "exe");

    private BridgeHost Host(string settingsJson = NoSims, BridgeOptions? options = null)
    {
        Directory.CreateDirectory(_data.Path);
        File.WriteAllText(Path.Combine(_data.Path, "settings.json"), settingsJson);
        options ??= new BridgeOptions { DataDirectory = _data.Path, UsePhysicalPanel = false, RecordingDirectory = ExeFolder };
        return new BridgeHost(options, NullLoggerFactory.Instance, () => { });
    }

    [Fact]
    public void Recordings_go_next_to_the_executable_by_default()
    {
        using var host = Host(options: new BridgeOptions { DataDirectory = _data.Path, UsePhysicalPanel = false });

        Assert.Equal(AppPaths.ExecutableDirectory, host.PreferredRecordingDirectory);
        Assert.Equal(AppPaths.ExecutableDirectory, host.RecordingDirectory); // what "Open folder" opens before the first recording
    }

    [Fact]
    public void Record_option_starts_a_recording_with_the_setup_in_its_first_line_and_closing_ends_it()
    {
        string file;
        using (var host = Host("""{ "MsfsEnabled": false, "XPlaneEnabled": false, "VfrSquawk": 1200 }""",
                   new BridgeOptions { DataDirectory = _data.Path, UsePhysicalPanel = false, RecordingDirectory = ExeFolder, Record = true }))
        {
            host.Start();
            Assert.True(host.Recorder.IsRecording);
            file = host.Recorder.FilePath!;
            Assert.Equal(ExeFolder, host.RecordingDirectory);
            host.Settings.Update(s => s with { VfrSquawk = 7000 });
        }

        Assert.Equal(ExeFolder, Path.GetDirectoryName(file));
        var events = TempFolder.ReadEvents(file);
        var header = events[0];
        Assert.Equal("start", header.GetProperty("ev").GetString());
        Assert.Equal(1200, header.GetProperty("settings").GetProperty("vfrSquawk").GetInt32());
        Assert.False(header.GetProperty("physicalPanel").GetBoolean());
        Assert.False(string.IsNullOrEmpty(header.GetProperty("os").GetString()));
        var changed = Assert.Single(events, e => e.GetProperty("src").GetString() == "settings");
        Assert.Equal(7000, changed.GetProperty("settings").GetProperty("vfrSquawk").GetInt32());
        Assert.Equal("stop", events[^1].GetProperty("ev").GetString());
    }

    [Fact]
    public void A_folder_that_cannot_be_written_falls_back_to_the_data_folder()
    {
        Directory.CreateDirectory(_data.Path);
        string blocker = Path.Combine(_data.Path, "a-file");
        File.WriteAllText(blocker, "");
        using var host = Host(options: new BridgeOptions
        {
            DataDirectory = _data.Path,
            UsePhysicalPanel = false,
            RecordingDirectory = Path.Combine(blocker, "recordings"), // under a file: can't exist
        });

        string? file = host.StartRecording();

        Assert.Equal(Path.Combine(_data.Path, "recordings"), Path.GetDirectoryName(file));
        Assert.Equal(Path.GetDirectoryName(file), host.RecordingDirectory);
        host.StopRecording();
    }

    [Fact]
    public void When_nothing_can_be_written_radiobridge_runs_on_without_a_recording()
    {
        // Real settings with no simulators (defaults would look for MSFS and X-Plane on this machine), and
        // both recording folders blocked: a file where the data folder's "recordings" folder would be.
        Directory.CreateDirectory(_data.Path);
        string blocker = Path.Combine(_data.Path, "recordings");
        File.WriteAllText(blocker, "");
        using var host = Host(options: new BridgeOptions
        {
            DataDirectory = _data.Path,
            UsePhysicalPanel = false,
            RecordingDirectory = Path.Combine(blocker, "exe"),
            Record = true,
        });

        host.Start(); // must not throw

        Assert.False(host.Recorder.IsRecording);
        Assert.Null(host.StartRecording());
    }

    [Fact]
    public void The_record_at_start_setting_starts_a_recording()
    {
        using var host = Host("""{ "MsfsEnabled": false, "XPlaneEnabled": false, "RecordAtStart": true }""");

        host.Start();

        Assert.True(host.Recorder.IsRecording);
    }

    [Fact]
    public void Nothing_is_recorded_by_default()
    {
        using (var host = Host())
        {
            Assert.False(host.Settings.Current.RecordAtStart);
            host.Start();
            Assert.False(host.Recorder.IsRecording);
        }

        Assert.False(Directory.Exists(ExeFolder));
        Assert.False(Directory.Exists(Path.Combine(_data.Path, "recordings")));
    }
}
