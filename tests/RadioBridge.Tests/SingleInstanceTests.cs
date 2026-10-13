using System.IO.Pipes;
using RadioBridge.Core;

namespace RadioBridge.Tests;

public sealed class SingleInstanceTests : IDisposable
{
    private static readonly Version Running = new(0, 1, 5);

    // A pipe of its own, so the tests never meet a real RadioBridge.
    private readonly string _pipe = $"RadioBridge.Test.{Guid.NewGuid():N}";
    private readonly CancellationTokenSource _stop = new();
    private int _shown;
    private Version? _quitFor;

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    private void Listen() => SingleInstance.Listen(Running, () => Interlocked.Increment(ref _shown), v => _quitFor = v, _stop.Token, _pipe);

    [Theory]
    [InlineData("0.1.3+7cf735cdacacca2224755812fcea5a136d74546b", "0.1.3")]
    [InlineData("0.1.4.0", "0.1.4")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("0.2.0-beta", "0.2.0")]
    public void Reads_product_and_file_versions(string text, string expected) =>
        Assert.Equal(Version.Parse(expected), SingleInstance.ParseVersion(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Microsoft® .NET")]
    public void Anything_else_is_no_version(string? text) => Assert.Null(SingleInstance.ParseVersion(text));

    [Theory]
    [InlineData("RadioBridge", "0.1.3+7cf735c", true)]
    [InlineData("RadioBridge", "0.1.5+abc", false)] // the same version
    [InlineData("RadioBridge", "0.2.0", false)]     // newer
    [InlineData("Microsoft® .NET", "0.0.1", false)] // another program that happens to share the process name (dotnet)
    [InlineData("RadioBridge", "", false)]
    public void Only_an_older_radiobridge_is_ever_stopped(string product, string version, bool stop) =>
        Assert.Equal(stop, SingleInstance.OlderRadioBridge(product, version, Running) is not null);

    [Fact]
    public async Task A_newer_launch_is_told_the_running_one_makes_way()
    {
        Listen();

        var reply = SingleInstance.AskRunning(new Version(0, 1, 6), pipeName: _pipe);

        Assert.Equal(new LaunchReply(LaunchAnswer.Quitting, Running), reply);
        await Wait.Until(() => _quitFor is not null, "the running one to quit");
        Assert.Equal(new Version(0, 1, 6), _quitFor);
        Assert.Equal(0, _shown);
    }

    [Theory]
    [InlineData("0.1.5")]
    [InlineData("0.1.4")]
    public async Task The_same_or_an_older_launch_brings_the_running_window_to_the_front(string launched)
    {
        Listen();

        var reply = SingleInstance.AskRunning(Version.Parse(launched), pipeName: _pipe);

        Assert.Equal(new LaunchReply(LaunchAnswer.Staying, Running), reply);
        await Wait.Until(() => _shown == 1, "the window to be shown");
        Assert.Null(_quitFor);
    }

    [Fact]
    public async Task A_launch_without_a_window_does_not_show_the_running_one()
    {
        Listen();

        var reply = SingleInstance.AskRunning(Running, show: false, pipeName: _pipe);
        await Task.Delay(300);

        Assert.Equal(LaunchAnswer.Staying, reply.Answer);
        Assert.Equal(0, _shown);
    }

    [Fact]
    public async Task A_launch_of_014_or_older_still_brings_the_window_to_the_front()
    {
        Listen();

        Assert.True(SingleInstance.AskLegacyToShow(_pipe));

        await Wait.Until(() => _shown == 1, "the window to be shown");
    }

    [Fact]
    public void Answers_keep_coming_after_the_first()
    {
        Listen();

        Assert.Equal(LaunchAnswer.Staying, SingleInstance.AskRunning(Running, pipeName: _pipe).Answer);
        Assert.Equal(LaunchAnswer.Staying, SingleInstance.AskRunning(Running, pipeName: _pipe).Answer);
        Assert.Equal(LaunchAnswer.Quitting, SingleInstance.AskRunning(new Version(1, 0, 0), pipeName: _pipe).Answer);
    }

    [SkippableFact]
    public async Task A_running_014_or_older_is_recognised_by_its_one_way_pipe()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "0.1.4 and older were only released for Windows, where a one-way pipe refuses a two-way client.");
        // What 0.1.4 and older listen with.
        await using var legacy = new NamedPipeServerStream(_pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var waiting = legacy.WaitForConnectionAsync(_stop.Token);

        var reply = SingleInstance.AskRunning(Running, pipeName: _pipe);

        Assert.Equal(new LaunchReply(LaunchAnswer.Legacy), reply);
        Assert.False(waiting.IsCompletedSuccessfully);
    }

    [Fact]
    public void Nothing_running_is_no_answer() =>
        Assert.Equal(LaunchAnswer.NoAnswer, SingleInstance.AskRunning(Running, pipeName: _pipe).Answer);
}
