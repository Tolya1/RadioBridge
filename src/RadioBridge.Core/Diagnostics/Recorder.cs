using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;

namespace RadioBridge.Core.Diagnostics;

/// <summary>
/// The debug recording: while it runs, everything that happens is written to a JSON Lines file, one event
/// per line. The panel's raw reports and what RadioBridge shows on its displays, what it decides, what it
/// sends to the simulators and every value they send back (for X-Plane also the radio datarefs of the
/// aircraft and its add-ons), settings changes and the log. Meant to be sent with a bug report.
/// <para>
/// Every line has <c>ms</c> (milliseconds since the recording started), <c>t</c> (local time), <c>src</c>
/// (panel, controller, sim, xplane, msfs, settings, log, recorder) and <c>ev</c>, plus fields of its own.
/// Recording costs nothing while it is off; while it is on, events are queued and written by a background task.
/// </para>
/// </summary>
public sealed class Recorder : IDisposable
{
    /// <summary>A recording stops by itself at this size, so a forgotten one can't fill the disk.</summary>
    public const long DefaultMaxBytes = 200L * 1024 * 1024;

    /// <summary>Recordings kept in the folder (the newest); older ones go when a new one starts, like old logs.</summary>
    public const int DefaultKeepFiles = 20;

    private const string FilePattern = "radiobridge-*.jsonl";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly object _gate = new();
    private volatile Session? _session;

    public Recorder(long maxBytes = DefaultMaxBytes, int keepFiles = DefaultKeepFiles)
    {
        MaxBytes = maxBytes;
        KeepFiles = keepFiles;
    }

    public long MaxBytes { get; }

    public int KeepFiles { get; }

    public bool IsRecording => _session is not null;

    /// <summary>The file being written, or the last one written after the recording stopped.</summary>
    public string? FilePath { get; private set; }

    /// <summary>Bytes written to <see cref="FilePath"/> so far.</summary>
    public long BytesWritten => _session?.Bytes ?? _lastBytes;

    private long _lastBytes;

    /// <summary>Raised (on any thread) when a recording starts or stops.</summary>
    public event Action? Changed;

    /// <summary>
    /// Starts a new recording file in <paramref name="directory"/> (or keeps the one running) and returns its path.
    /// <paramref name="header"/> adds fields to the first line, such as the version and the settings.
    /// </summary>
    public string Start(string directory, Action<Utf8JsonWriter>? header = null)
    {
        lock (_gate)
        {
            if (_session is { } running)
                return running.Path;

            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"radiobridge-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
            for (int n = 2; File.Exists(path); n++)
                path = Path.Combine(directory, $"radiobridge-{DateTime.Now:yyyyMMdd-HHmmss}-{n}.jsonl");

            // Shared for reading, so the file can be opened while it is being written.
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var session = new Session(path, stream, Stopwatch.GetTimestamp());
            var removed = RemoveOldRecordings(directory, path);
            // The header is queued before the session is visible to other threads, so it is always the first line.
            session.Queue.Writer.TryWrite(Format(session, "recorder", "start", w =>
            {
                w.WriteString("started", DateTimeOffset.Now.ToString("o", Inv));
                if (removed > 0)
                    w.WriteNumber("removedOldRecordings", removed);
                header?.Invoke(w);
            }));
            session.Writer = Task.Run(() => WriteLoopAsync(session));
            _session = session;
            FilePath = path;
        }
        Changed?.Invoke();
        return FilePath!;
    }

    /// <summary>Deletes all but the newest <see cref="KeepFiles"/> recordings (counting the new one).</summary>
    private int RemoveOldRecordings(string directory, string current)
    {
        int removed = 0;
        FileInfo[] all;
        try
        {
            all = new DirectoryInfo(directory).GetFiles(FilePattern);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0; // tidying up is a nicety; the recording itself is running
        }
        var old = all
            .Where(f => !string.Equals(f.FullName, Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Skip(Math.Max(0, KeepFiles - 1));
        foreach (var file in old)
        {
            try
            {
                file.Delete();
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Open in an editor, say: it stays, and goes next time.
            }
        }
        return removed;
    }

    /// <summary>Stops the recording, writing everything still queued. Does nothing when not recording.</summary>
    public void Stop() => Stop("stopped", wait: true);

    /// <param name="wait">Wait until the file is complete; not from the writer itself, which would wait for itself.</param>
    private void Stop(string reason, bool wait)
    {
        Session session;
        lock (_gate)
        {
            if (_session is not { } running)
                return;
            Record("recorder", "stop", w => w.WriteString("reason", reason));
            _session = null;
            session = running;
        }

        session.Queue.Writer.TryComplete();
        if (wait)
        {
            try { session.Writer!.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Adds one event. Cheap and does nothing while not recording; safe from any thread. <paramref name="fields"/>
    /// runs before this returns, so it may read buffers the caller is about to reuse.
    /// </summary>
    public void Record(string source, string ev, Action<Utf8JsonWriter>? fields = null)
    {
        var session = _session;
        if (session is null)
            return;
        session.Queue.Writer.TryWrite(Format(session, source, ev, fields));
    }

    /// <summary>One line: the common fields, then the event's own.</summary>
    private static byte[] Format(Session session, string source, string ev, Action<Utf8JsonWriter>? fields)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("ms", Math.Round(Stopwatch.GetElapsedTime(session.Started).TotalMilliseconds, 1));
            w.WriteString("t", DateTime.Now.ToString("HH:mm:ss.fff", Inv));
            w.WriteString("src", source);
            w.WriteString("ev", ev);
            try
            {
                fields?.Invoke(w);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // A field the writer refused (a broken value): keep the event and say why, rather than lose it.
                w.WriteString("recordError", ex.Message);
            }
            w.WriteEndObject();
        }
        buffer.Write("\n"u8);
        return buffer.WrittenSpan.ToArray();
    }

    private async Task WriteLoopAsync(Session session)
    {
        var reader = session.Queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync())
            {
                while (reader.TryRead(out var line))
                {
                    await session.Stream.WriteAsync(line);
                    session.Bytes += line.Length;
                }
                await session.Stream.FlushAsync(); // flushed whenever the queue runs dry, so a crash loses little

                if (session.Bytes >= MaxBytes && _session == session)
                    Stop($"the file reached {MaxBytes / (1024 * 1024)} MB", wait: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Disk full or the file went away: the recording ends, the app carries on.
            bool wasCurrent;
            lock (_gate)
            {
                wasCurrent = _session == session;
                if (wasCurrent)
                    _session = null;
            }
            session.Queue.Writer.TryComplete();
            if (wasCurrent)
                Changed?.Invoke();
        }
        finally
        {
            Interlocked.Exchange(ref _lastBytes, session.Bytes);
            await session.Stream.DisposeAsync();
        }
    }

    public void Dispose() => Stop("RadioBridge closed", wait: true);

    private sealed class Session(string path, FileStream stream, long started)
    {
        public string Path { get; } = path;
        public FileStream Stream { get; } = stream;
        public long Started { get; } = started;
        public Channel<byte[]> Queue { get; } = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        public Task? Writer { get; set; }
        public long Bytes;
    }
}

/// <summary>Helpers for writing event fields.</summary>
public static class RecorderJson
{
    /// <summary>Writes a number, or null for NaN and infinities (which JSON can't hold).</summary>
    public static void WriteNumberOrNull(this Utf8JsonWriter w, string name, double value)
    {
        if (double.IsFinite(value))
            w.WriteNumber(name, value);
        else
            w.WriteNull(name);
    }

    /// <summary>The radio values as RadioBridge understands them, in kHz, hPa and nautical miles.</summary>
    public static void WriteRadioState(this Utf8JsonWriter w, string name, Sim.RadioState s)
    {
        w.WriteStartObject(name);
        WritePair(w, "com1", s.Com1);
        WritePair(w, "com2", s.Com2);
        WritePair(w, "nav1", s.Nav1);
        WritePair(w, "nav2", s.Nav2);
        w.WriteNumber("adf1", s.Adf1Khz);
        w.WriteNumber("adf2", s.Adf2Khz);
        w.WriteNumberOrNull("dme1", s.Dme1Nm);
        w.WriteNumberOrNull("dme2", s.Dme2Nm);
        w.WriteNumber("squawk", s.Squawk);
        w.WriteNumberOrNull("baroHpa", s.BaroHpa);
        WriteGps(w, "gps1", s.Gps1);
        WriteGps(w, "gps2", s.Gps2);
        w.WriteEndObject();
    }

    private static void WriteGps(Utf8JsonWriter w, string name, Sim.GpsNav gps)
    {
        w.WriteStartObject(name);
        w.WriteBoolean("waypoint", gps.HasWaypoint);
        w.WriteNumberOrNull("distanceNm", gps.DistanceNm);
        w.WriteNumberOrNull("trackDeg", gps.TrackDeg);
        w.WriteEndObject();
    }

    private static void WritePair(Utf8JsonWriter w, string name, Sim.RadioPair pair)
    {
        w.WriteStartArray(name);
        w.WriteNumberValue(pair.ActiveKhz);
        w.WriteNumberValue(pair.StandbyKhz);
        w.WriteEndArray();
    }
}
