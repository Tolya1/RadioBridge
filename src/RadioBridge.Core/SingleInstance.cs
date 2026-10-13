using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace RadioBridge.Core;

/// <summary>What the running RadioBridge answered when another one was launched.</summary>
public enum LaunchAnswer
{
    /// <summary>It keeps running (it is the same version or newer) and showed its window if asked to.</summary>
    Staying,

    /// <summary>It is older than the one being launched, and quits to make way for it.</summary>
    Quitting,

    /// <summary>0.1.4 or older: it only understands "show your window" and can't be asked about versions.</summary>
    Legacy,

    /// <summary>Nothing answered.</summary>
    NoAnswer,
}

/// <param name="Running">The running instance's version, when it said (not 0.1.4 and older).</param>
public sealed record LaunchReply(LaunchAnswer Answer, Version? Running = null);

/// <summary>
/// One RadioBridge per user session, which a mutex in Program guards. Launching it again brings the running
/// window to the front instead of starting a second copy that would fight over the panel, unless the launch
/// is a newer version: then the running one quits and the new one takes over, so starting a new download just
/// works.
/// <para>
/// The running instance listens on a named pipe. 0.1.4 and older listen one way only and know a single
/// message, "show your window" (<see cref="ShowWindow"/>); a two-way connection to them is refused, which is
/// how a launch tells them apart (<see cref="LaunchAnswer.Legacy"/>). Newer ones answer <see cref="Launching"/>
/// with whether they stay or quit.
/// </para>
/// </summary>
public static class SingleInstance
{
    /// <summary>
    /// Testing only: a name that gives test runs a single-instance mutex and pipe of their own, so they neither
    /// meet nor replace the RadioBridge the user is running.
    /// </summary>
    public const string TestInstanceVariable = "RADIOBRIDGE_TEST_INSTANCE";

    private static readonly string Suffix = Environment.GetEnvironmentVariable(TestInstanceVariable) is { Length: > 0 } test ? "." + test : "";

    public static readonly string MutexName = "RadioBridge.SingleInstance" + Suffix;

    public static readonly string PipeName = PipeNameFor(Environment.GetEnvironmentVariable(TestInstanceVariable));

    /// <summary>The pipe of the instance named <paramref name="testInstance"/> (<see cref="TestInstanceVariable"/>), or the real one.</summary>
    public static string PipeNameFor(string? testInstance) =>
        $"RadioBridge.Activate.{Environment.UserName}" + (string.IsNullOrEmpty(testInstance) ? "" : "." + testInstance);

    /// <summary>The whole message of 0.1.4 and older launches: show the window.</summary>
    private const byte ShowWindow = 1;

    /// <summary>
    /// Followed by a flags byte (<see cref="ShowIfStaying"/>) and the launched version. The answer is
    /// <see cref="AnswerStaying"/> or <see cref="AnswerQuitting"/> and the running version. A version goes as a
    /// length byte and UTF-8 text.
    /// </summary>
    private const byte Launching = 2;

    private const byte ShowIfStaying = 1;
    private const byte AnswerStaying = (byte)'S';
    private const byte AnswerQuitting = (byte)'Q';
    private const int MaxVersionLength = 32;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>Major.minor.patch: the fourth part of an assembly version is always 0 here.</summary>
    public static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0));

    /// <summary>Reads "0.1.3+7cf735c…" (a product version) or "0.1.3.0"; null when it isn't a version.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        int end = text.IndexOfAny(['+', '-', ' ']);
        return Version.TryParse(end < 0 ? text : text[..end], out var version) ? Normalize(version) : null;
    }

    /// <summary>
    /// Tells the running instance that <paramref name="version"/> is being launched. An older one answers
    /// <see cref="LaunchAnswer.Quitting"/> and quits; any other shows its window when <paramref name="show"/>.
    /// </summary>
    public static LaunchReply AskRunning(Version version, bool show = true, string? pipeName = null)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName ?? PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(Timeout);
            pipe.Write([Launching, show ? ShowIfStaying : (byte)0, .. VersionBytes(version)]);
            pipe.Flush();

            using var timeout = new CancellationTokenSource(Timeout);
            var answer = ReadAsync(pipe, 1, timeout.Token).GetAwaiter().GetResult();
            var running = ReadVersionAsync(pipe, timeout.Token).GetAwaiter().GetResult();
            return answer switch
            {
                [AnswerQuitting] => new LaunchReply(LaunchAnswer.Quitting, running),
                [AnswerStaying] => new LaunchReply(LaunchAnswer.Staying, running),
                _ => new LaunchReply(LaunchAnswer.NoAnswer),
            };
        }
        catch (UnauthorizedAccessException)
        {
            return new LaunchReply(LaunchAnswer.Legacy); // its pipe is one way: 0.1.4 or older
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
        {
            return new LaunchReply(LaunchAnswer.NoAnswer);
        }
    }

    private static byte[] VersionBytes(Version version)
    {
        byte[] text = Encoding.UTF8.GetBytes(Normalize(version).ToString());
        return [(byte)text.Length, .. text];
    }

    private static async Task<Version?> ReadVersionAsync(Stream stream, CancellationToken ct) =>
        await ReadAsync(stream, 1, ct) is [var length and <= MaxVersionLength] && await ReadAsync(stream, length, ct) is { } text
            ? ParseVersion(Encoding.UTF8.GetString(text))
            : null;

    /// <summary>Asks a 0.1.4-or-older instance to show its window. False if none answered.</summary>
    public static bool AskLegacyToShow(string? pipeName = null)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName ?? PipeName, PipeDirection.Out);
            pipe.Connect(Timeout);
            pipe.WriteByte(ShowWindow);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Answers later launches until <paramref name="ct"/> ends, on a background thread: a newer version is told
    /// this one quits and <paramref name="quitForNewer"/> is called; otherwise <paramref name="show"/> is (when the
    /// launch asks for the window, as a launch of 0.1.4 or older always does).
    /// </summary>
    public static void Listen(Version version, Action show, Action<Version> quitForNewer, CancellationToken ct, string? pipeName = null) =>
        _ = Task.Run(async () =>
        {
            var mine = Normalize(version);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(pipeName ?? PipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(ct);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(Timeout);
                    var message = await ReadAsync(server, 1, timeout.Token);
                    if (message is [ShowWindow])
                    {
                        show();
                        continue;
                    }
                    if (message is not [Launching] || await ReadAsync(server, 1, timeout.Token) is not [var flags]
                        || await ReadVersionAsync(server, timeout.Token) is not { } launched)
                    {
                        continue;
                    }

                    bool quit = launched > mine;
                    await server.WriteAsync((byte[])[quit ? AnswerQuitting : AnswerStaying, .. VersionBytes(mine)], timeout.Token);
                    await server.FlushAsync(timeout.Token);
                    // Wait for the launch to hang up, i.e. to have read the answer: quitting before that could
                    // leave it without one, and then it would give up while this one goes too.
                    await ReadAsync(server, 1, timeout.Token);
                    if (quit)
                        quitForNewer(launched);
                    else if ((flags & ShowIfStaying) != 0)
                        show();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                    // A launch that went away mid-message or never hung up, or a pipe that couldn't be created right now.
                    await Task.Delay(TimeSpan.FromMilliseconds(200), ct).ContinueWith(_ => { });
                }
            }
        }, ct);

    /// <summary>Reads exactly <paramref name="count"/> bytes; null if the other side closed first.</summary>
    private static async Task<byte[]?> ReadAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        int done = 0;
        while (done < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(done), ct);
            if (read == 0)
                return null;
            done += read;
        }
        return buffer;
    }

    /// <summary>
    /// Stops this user's RadioBridge processes older than <paramref name="version"/>: the way to replace 0.1.4 and
    /// older, which can't be asked to quit. Only processes whose executable says it is RadioBridge and names an
    /// older version are touched. Windows only (there were no releases for other systems). Returns the versions stopped.
    /// </summary>
    public static IReadOnlyList<Version> StopOlderLegacy(Version version)
    {
        var stopped = new List<Version>();
        if (!OperatingSystem.IsWindows())
            return stopped;
        using var self = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(self.ProcessName))
        {
            using (process)
            {
                if (process.Id == self.Id || process.SessionId != self.SessionId)
                    continue;
                FileVersionInfo? info;
                try
                {
                    info = process.MainModule?.FileVersionInfo;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    continue; // gone, or not ours to inspect
                }
                if (OlderRadioBridge(info?.ProductName, info?.ProductVersion, version) is not { } older)
                    continue;
                try
                {
                    process.Kill();
                    process.WaitForExit(TimeSpan.FromSeconds(5));
                    stopped.Add(older);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // it went away by itself, or can't be stopped: the mutex wait will tell
                }
            }
        }
        return stopped;
    }

    /// <summary>The version of a process's executable, if it is RadioBridge and older than <paramref name="version"/>.</summary>
    internal static Version? OlderRadioBridge(string? productName, string? productVersion, Version version) =>
        productName == "RadioBridge" && ParseVersion(productVersion) is { } theirs && theirs < Normalize(version) ? theirs : null;
}
