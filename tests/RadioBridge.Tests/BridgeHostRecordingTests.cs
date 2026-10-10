using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RadioBridge.Core;

namespace RadioBridge.Tests;

public sealed class BridgeHostRecordingTests : IDisposable
{
    private readonly TempFolder _data = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public void Record_option_starts_a_recording_with_the_setup_in_its_first_line_and_closing_ends_it()
    {
        Directory.CreateDirectory(_data.Path);
        File.WriteAllText(Path.Combine(_data.Path, "settings.json"), """{ "MsfsEnabled": false, "XPlaneEnabled": false, "VfrSquawk": 1200 }""");
        var options = new BridgeOptions { DataDirectory = _data.Path, UsePhysicalPanel = false, Record = true };

        string file;
        using (var host = new BridgeHost(options, NullLoggerFactory.Instance, () => { }))
        {
            host.Start();
            Assert.True(host.Recorder.IsRecording);
            file = host.Recorder.FilePath!;
            host.Settings.Update(s => s with { VfrSquawk = 7000 });
        }

        Assert.StartsWith(Path.Combine(_data.Path, "recordings"), file);
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
}
