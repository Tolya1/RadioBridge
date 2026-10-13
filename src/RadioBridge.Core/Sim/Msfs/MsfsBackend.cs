using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RadioBridge.Core.Diagnostics;
using RadioBridge.Core.Radio;
using static RadioBridge.Core.Sim.Msfs.SimConnectNative;

namespace RadioBridge.Core.Sim.Msfs;

/// <summary>
/// MSFS 2020/2024 through SimConnect (Windows only). One thread owns the connection: it polls for
/// messages and drains queued commands, because SimConnect calls must not overlap. Values are
/// requested every sim frame but only sent by MSFS when they change.
/// </summary>
public sealed class MsfsBackend : ISimBackend
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    // Read definition: one FLOAT64 per variable, in this order.
    private static readonly (string Name, string Unit)[] Variables =
    [
        ("COM ACTIVE FREQUENCY:1", "MHz"),
        ("COM STANDBY FREQUENCY:1", "MHz"),
        ("COM ACTIVE FREQUENCY:2", "MHz"),
        ("COM STANDBY FREQUENCY:2", "MHz"),
        ("NAV ACTIVE FREQUENCY:1", "MHz"),
        ("NAV STANDBY FREQUENCY:1", "MHz"),
        ("NAV ACTIVE FREQUENCY:2", "MHz"),
        ("NAV STANDBY FREQUENCY:2", "MHz"),
        ("ADF ACTIVE FREQUENCY:1", "KHz"),
        ("ADF ACTIVE FREQUENCY:2", "KHz"),
        ("NAV DME:1", "Nautical miles"),
        ("NAV DME:2", "Nautical miles"),
        ("NAV HAS DME:1", "Bool"),
        ("NAV HAS DME:2", "Bool"),
        ("TRANSPONDER CODE:1", "BCO16"),
        ("KOHLSMAN SETTING MB:1", "Millibars"),
    ];

    // The GPS that drives navigation; MSFS has no such values per GNS unit, so they are shown as GPS 1's.
    // A request of their own, so a GPS value MSFS might not know can't shift the radio values.
    private static readonly (string Name, string Unit)[] GpsVariables =
    [
        ("GPS WP DISTANCE", "Nautical miles"),
        ("GPS WP DESIRED TRACK", "Degrees"),
        ("GPS IS ACTIVE WAY POINT", "Bool"),
    ];

    private const uint DefineGps = 1;
    private const uint RequestGps = 1;

    // The aircraft's title and .cfg path, to know a PMDG 737 (Pmdg737).
    private const uint DefineTitle = 2;
    private const uint RequestTitle = 2;
    private const uint RequestAircraft = 3;
    private const int TitleLength = 256;

    /// <summary>A NUL-terminated string from SimConnect.</summary>
    private static string CString(byte[] bytes) =>
        System.Text.Encoding.UTF8.GetString(bytes, 0, Array.IndexOf(bytes, (byte)0) is var end and >= 0 ? end : bytes.Length);

    /// <summary>Client event IDs for PMDG's events: this plus the event's number.</summary>
    private const uint PmdgClientEvents = 1000;

    /// <summary>How often the aircraft's input events are listed again, which also notices another aircraft.</summary>
    private static readonly TimeSpan InputEventsInterval = TimeSpan.FromSeconds(10);

    private const uint RequestInputEvents = 100; // + a running number

    private enum Ev : uint
    {
        Com1Standby, Com2Standby, Nav1Standby, Nav2Standby,
        Com1Swap, Com2Swap, Nav1Swap, Nav2Swap,
        Adf1Set, Adf2Set, Squawk, Kohlsman, BaroStandard,
    }

    // Frequencies go in Hz; ADF as BCD-encoded Hz, squawk as BCO16, Kohlsman as millibars * 16.
    private static readonly Dictionary<Ev, string> EventNames = new()
    {
        [Ev.Com1Standby] = "COM_STBY_RADIO_SET_HZ",
        [Ev.Com2Standby] = "COM2_STBY_RADIO_SET_HZ",
        [Ev.Nav1Standby] = "NAV1_STBY_SET_HZ",
        [Ev.Nav2Standby] = "NAV2_STBY_SET_HZ",
        [Ev.Com1Swap] = "COM1_RADIO_SWAP",
        [Ev.Com2Swap] = "COM2_RADIO_SWAP",
        [Ev.Nav1Swap] = "NAV1_RADIO_SWAP",
        [Ev.Nav2Swap] = "NAV2_RADIO_SWAP",
        [Ev.Adf1Set] = "ADF_COMPLETE_SET",
        [Ev.Adf2Set] = "ADF2_COMPLETE_SET",
        [Ev.Squawk] = "XPNDR_SET",
        [Ev.Kohlsman] = "KOHLSMAN_SET",
        [Ev.BaroStandard] = "BAROMETRIC_STD_PRESSURE",
    };

    private const uint DefineRadios = 0;
    private const uint RequestRadios = 0;

    private readonly ILogger _log;
    private readonly Recorder _recorder;
    private readonly Func<string?> _configuredPath;
    private readonly ILocatorEnvironment _environment;
    private readonly ConcurrentQueue<(Ev Event, uint Data)> _outbox = new();
    private readonly ConcurrentQueue<InputEventSet> _inputs = new();
    private readonly ConcurrentQueue<(uint Event, uint Parameter)> _pmdgOutbox = new();
    private volatile GnsInputEvents _gns = GnsInputEvents.None;
    private volatile bool _pmdg737;
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<string> _loggedProblems = new(StringComparer.OrdinalIgnoreCase);
    private Thread? _thread;
    private volatile bool _connected;
    private volatile RadioState _state = RadioState.Empty;
    private SimNotice? _notice;

    /// <param name="configuredPath">The SimConnect library chosen in the settings, read on every attempt (null = automatic).</param>
    /// <param name="environment">The machine to search; the real one unless a test supplies another.</param>
    public MsfsBackend(Func<string?>? configuredPath = null, ILocatorEnvironment? environment = null, ILogger<MsfsBackend>? log = null,
        Recorder? recorder = null)
    {
        _configuredPath = configuredPath ?? (() => null);
        _environment = environment ?? new MachineEnvironment();
        _log = log ?? NullLogger<MsfsBackend>.Instance;
        _recorder = recorder ?? new Recorder();
    }

    public string Name => "MSFS";
    public bool IsConnected => _connected;
    public RadioState State => _state;

    public SimNotice? Notice => Volatile.Read(ref _notice);

    /// <summary>When the loaded aircraft has the stock GNS 430/530, whose keys are input events (<see cref="GnsInputEvents"/>).</summary>
    public bool SupportsGpsKeys => _connected && _gns.Units.Count > 0;

    /// <summary>A PMDG 737 is loaded: its radios only react to its own events, which turn knobs (<see cref="Pmdg737"/>).</summary>
    public bool TurnsKnobs => _connected && _pmdg737;

    public event Action? Changed;

    public void Start()
    {
        if (_thread is not null)
            return;
        if (!OperatingSystem.IsWindows())
        {
            _log.LogInformation("MSFS support is Windows-only; skipping SimConnect.");
            return;
        }

        // The thread starts even without a SimConnect library: it keeps looking, so starting RadioBridge
        // before MSFS (or fixing the setting later) works without a restart.
        _thread = new Thread(Run) { IsBackground = true, Name = "SimConnect" };
        _thread.Start();
    }

    /// <summary>
    /// Makes sure a SimConnect library is loaded: tries every library the locator finds, in order, until one
    /// loads. Returns false (and sets <see cref="Notice"/> when it matters) if none does.
    /// </summary>
    private bool EnsureLibrary()
    {
        if (LoadedPath is not null)
        {
            SetNotice(null);
            return true;
        }

        string? configured = _configuredPath();
        var found = SimConnectLocator.FindAll(configured, _environment).ToList();
        foreach (var candidate in found)
        {
            if (TryLoad(candidate.Path, out string? error))
            {
                _log.LogInformation("Using {Path} (from {Source}).", candidate.Path, candidate.Source);
                SetNotice(null);
                return true;
            }
            LogOnce($"load:{candidate.Path}", () => _log.LogWarning(
                "{Path} (from {Source}) could not be loaded: {Error} It must be the 64-bit SimConnect library.", candidate.Path, candidate.Source, error));
        }

        // Nothing usable. That only matters once MSFS is there to connect to: lots of RadioBridge users fly
        // X-Plane only, and a warning for them on every start would be noise.
        if (found.Count > 0)
        {
            SetNotice(new SimNotice(
                $"MSFS can't be reached: {Path.GetFileName(found[0].Path)} isn't a usable 64-bit SimConnect library.", CanLocateSimConnect: true));
        }
        else if (_environment.IsMsfsRunning)
        {
            SetNotice(new SimNotice("MSFS is running, but no SimConnect library was found.", CanLocateSimConnect: true));
            LogOnce("missing", () => _log.LogWarning("MSFS is running but no SimConnect library was found. Searched: {Paths}",
                string.Join("; ", SimConnectLocator.Candidates(configured, _environment).Select(c => c.Path))));
        }
        else
        {
            SetNotice(null);
        }
        return false;
    }

    private void LogOnce(string key, Action log)
    {
        bool first;
        lock (_loggedProblems)
            first = _loggedProblems.Add(key);
        if (first)
            log();
    }

    private void SetNotice(SimNotice? notice)
    {
        if (Interlocked.Exchange(ref _notice, notice) != notice)
            Changed?.Invoke();
    }

    public void Send(SimCommand command)
    {
        if (!_connected)
            return;

        if (command is PressGpsKey gps)
        {
            foreach (var set in _gns.Sets(gps.Unit, gps.Key))
                _inputs.Enqueue(set);
            return;
        }

        if (_pmdg737 && command is TurnKnobs or SwapRadio or SetBaroStandard)
        {
            var events = Pmdg737.Translate(command, _state);
            if (events.Count == 0)
                LogOnce($"pmdg:{command.GetType().Name}:{(command as TurnKnobs)?.Target}", () => _log.LogInformation("The PMDG 737 has nothing for {Command}.", command));
            foreach (var e in events)
                _pmdgOutbox.Enqueue(e);
            return;
        }
        if (command is TurnKnobs)
            return; // the 737 was left a moment ago: the next turn comes as a value again

        (Ev, uint)? message = command switch
        {
            SetStandby c => (c.Radio switch
            {
                Tunable.Com1 => Ev.Com1Standby,
                Tunable.Com2 => Ev.Com2Standby,
                Tunable.Nav1 => Ev.Nav1Standby,
                _ => Ev.Nav2Standby,
            }, (uint)c.Khz * 1000),
            SwapRadio c => (c.Radio switch
            {
                Tunable.Com1 => Ev.Com1Swap,
                Tunable.Com2 => Ev.Com2Swap,
                Tunable.Nav1 => Ev.Nav1Swap,
                _ => Ev.Nav2Swap,
            }, 0u),
            SetAdf c => (c.Index == 2 ? Ev.Adf2Set : Ev.Adf1Set, ToBcd((uint)c.Khz * 1000)),
            SetSquawk c => (Ev.Squawk, Squawk.ToBco16(c.Code)),
            SetBaro c => (Ev.Kohlsman, (uint)Math.Round(c.Hpa * 16)),
            SetBaroStandard => (Ev.BaroStandard, 0u),
            _ => null,
        };

        if (message is { } m)
            _outbox.Enqueue(m);
    }

    private void Run()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            if (!EnsureLibrary())
            {
                ct.WaitHandle.WaitOne(RetryDelay);
                continue;
            }

            IntPtr handle = IntPtr.Zero;
            try
            {
                if (Open(out handle, "RadioBridge", IntPtr.Zero, 0, IntPtr.Zero, 0) != 0)
                {
                    handle = IntPtr.Zero; // MSFS is not running
                }
                else
                {
                    Configure(handle);
                    Pump(handle, ct);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                // Loaded, but it isn't a SimConnect client library (a function is missing): stop here, because
                // a native library can't be unloaded and swapped for another in this process.
                _log.LogError(ex, "{Path} is not a usable SimConnect library. MSFS is disabled until RadioBridge restarts.", LoadedPath);
                SetNotice(new SimNotice($"MSFS can't be reached: {Path.GetFileName(LoadedPath)} isn't a usable SimConnect library. Restart RadioBridge after choosing another.", CanLocateSimConnect: true));
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "SimConnect connection failed.");
            }
            finally
            {
                if (handle != IntPtr.Zero)
                    Close(handle);
                SetDisconnected();
            }

            ct.WaitHandle.WaitOne(RetryDelay);
        }
    }

    private static void Configure(IntPtr handle)
    {
        foreach (var (name, unit) in Variables)
            AddToDataDefinition(handle, DefineRadios, name, unit, DataTypeFloat64, 0f, Unused);
        foreach (var (name, unit) in GpsVariables)
            AddToDataDefinition(handle, DefineGps, name, unit, DataTypeFloat64, 0f, Unused);
        AddToDataDefinition(handle, DefineTitle, "TITLE", null, DataTypeString256, 0f, Unused);

        foreach (var (ev, name) in EventNames)
            MapClientEventToSimEvent(handle, (uint)ev, name);
        foreach (uint pmdg in Pmdg737.Events)
            MapClientEventToSimEvent(handle, PmdgClientEvents + pmdg, $"#{Pmdg737.EventBase + pmdg}");

        RequestDataOnSimObject(handle, RequestRadios, DefineRadios, ObjectIdUser, PeriodSimFrame, DataRequestFlagChanged, 0, 0, 0);
        RequestDataOnSimObject(handle, RequestGps, DefineGps, ObjectIdUser, PeriodSimFrame, DataRequestFlagChanged, 0, 0, 0);
        RequestDataOnSimObject(handle, RequestTitle, DefineTitle, ObjectIdUser, PeriodSecond, DataRequestFlagChanged, 0, 0, 0);
    }

    private void Pump(IntPtr handle, CancellationToken ct)
    {
        var values = new double[Variables.Length];
        var gpsValues = new double[GpsVariables.Length];
        var gps = GpsNav.None;
        bool open = true;

        // The aircraft's input events are listed now and then (the GNS keys are among them). A listing that
        // never completes means the aircraft has none: MSFS answers that with an exception instead.
        bool canListInputEvents = HasInputEvents;
        uint listing = 0;
        bool listed = true;
        var listedEvents = new List<(string Name, ulong Hash)>();
        long nextListing = long.MaxValue;
        long nextAircraftCheck = 0; // the loaded aircraft's path: at once, then every 10 s
        _aircraftTitle = _aircraftPath = null;

        while (open && !ct.IsCancellationRequested)
        {
            while (GetNextDispatch(handle, out IntPtr data, out uint size) == 0 && data != IntPtr.Zero)
            {
                switch ((uint)Marshal.ReadInt32(data, RecvIdOffset))
                {
                    case RecvOpen:
                        _connected = true;
                        _log.LogInformation("Connected to MSFS.");
                        _recorder.Record("msfs", "connected", w => w.WriteString("library", LoadedPath));
                        if (canListInputEvents)
                            nextListing = Stopwatch.GetTimestamp() + Stopwatch.Frequency; // in a second
                        else
                            _log.LogInformation("{Path} has no input events (MSFS 2020 before SU13): GPS keys can't be pressed.", LoadedPath);
                        Changed?.Invoke();
                        break;

                    case RecvEnumerateInputEvents:
                        var (request, page, pages, events) = GnsInputEvents.ReadPage(data, size);
                        if (request != RequestInputEvents + listing || listed)
                            break;
                        listedEvents.AddRange(events);
                        if (page + 1 >= pages)
                        {
                            listed = true;
                            UseGns(new GnsInputEvents(listedEvents), listedEvents.Count);
                        }
                        break;

                    case RecvQuit:
                        _log.LogInformation("MSFS closed the connection.");
                        _recorder.Record("msfs", "quit");
                        open = false;
                        break;

                    case RecvException:
                        int code = Marshal.ReadInt32(data, ExceptionCodeOffset);
                        _log.LogDebug("SimConnect exception {Code}.", code);
                        _recorder.Record("msfs", "exception", w => w.WriteNumber("code", code));
                        break;

                    case RecvSimObjectData when (uint)Marshal.ReadInt32(data, SimObjectRequestIdOffset) == RequestRadios:
                        Marshal.Copy(IntPtr.Add(data, SimObjectDataOffset), values, 0, values.Length);
                        _state = ToState(values) with { Gps1 = gps };
                        RecordValues(Variables, values);
                        Changed?.Invoke();
                        break;

                    case RecvSimObjectData when (uint)Marshal.ReadInt32(data, SimObjectRequestIdOffset) == RequestTitle:
                        var title = new byte[TitleLength];
                        Marshal.Copy(IntPtr.Add(data, SimObjectDataOffset), title, 0, title.Length);
                        UseAircraft(title: CString(title));
                        nextAircraftCheck = Stopwatch.GetTimestamp(); // another aircraft: its path now
                        break;

                    case RecvSystemState when (uint)Marshal.ReadInt32(data, SystemStateRequestIdOffset) == RequestAircraft:
                        var path = new byte[260];
                        Marshal.Copy(IntPtr.Add(data, SystemStateStringOffset), path, 0, path.Length);
                        UseAircraft(path: CString(path));
                        break;

                    case RecvSimObjectData when (uint)Marshal.ReadInt32(data, SimObjectRequestIdOffset) == RequestGps:
                        Marshal.Copy(IntPtr.Add(data, SimObjectDataOffset), gpsValues, 0, gpsValues.Length);
                        gps = ToGps(gpsValues);
                        _state = _state with { Gps1 = gps };
                        RecordValues(GpsVariables, gpsValues);
                        Changed?.Invoke();
                        break;
                }

                if (!open)
                    break;
            }

            while (_outbox.TryDequeue(out var message))
            {
                _log.LogDebug("MSFS <- {Event} {Data}", EventNames[message.Event], message.Data);
                TransmitClientEvent(handle, ObjectIdUser, (uint)message.Event, message.Data, GroupPriorityHighest, EventFlagGroupIdIsPriority);
                _recorder.Record("msfs", "event", w =>
                {
                    w.WriteString("event", EventNames[message.Event]);
                    w.WriteNumber("data", message.Data);
                });
            }

            while (_pmdgOutbox.TryDequeue(out var pmdg))
            {
                _log.LogDebug("MSFS <- PMDG event {Event} {Parameter:X}", pmdg.Event, pmdg.Parameter);
                TransmitClientEvent(handle, ObjectIdUser, PmdgClientEvents + pmdg.Event, pmdg.Parameter, GroupPriorityHighest, EventFlagGroupIdIsPriority);
                _recorder.Record("msfs", "pmdg", w =>
                {
                    w.WriteNumber("event", pmdg.Event);
                    w.WriteString("parameter", $"0x{pmdg.Parameter:X}");
                });
            }

            while (_inputs.TryDequeue(out var set))
            {
                _log.LogDebug("MSFS <- input event {Name} {Values}", set.Name, string.Join(" ", set.Values));
                SetInputEvent(handle, set.Hash, (uint)(set.Values.Length * sizeof(double)), set.Values);
                _recorder.Record("msfs", "input", w =>
                {
                    w.WriteString("name", set.Name);
                    w.WriteStartArray("values");
                    foreach (double value in set.Values)
                        w.WriteNumberValue(value);
                    w.WriteEndArray();
                });
            }

            if (_connected && Stopwatch.GetTimestamp() >= nextAircraftCheck)
            {
                RequestSystemState(handle, RequestAircraft, "AircraftLoaded");
                nextAircraftCheck = Stopwatch.GetTimestamp() + (long)(InputEventsInterval.TotalSeconds * Stopwatch.Frequency);
            }

            if (Stopwatch.GetTimestamp() >= nextListing)
            {
                if (!listed)
                    UseGns(GnsInputEvents.None, 0); // the last listing never came: this aircraft has no input events
                listing++;
                listed = false;
                listedEvents.Clear();
                EnumerateInputEvents(handle, RequestInputEvents + listing);
                nextListing = Stopwatch.GetTimestamp() + (long)(InputEventsInterval.TotalSeconds * Stopwatch.Frequency);
            }

            ct.WaitHandle.WaitOne(15);
        }
    }

    /// <summary>
    /// The loaded aircraft's title or .cfg path came (on connecting, when another aircraft is loaded, and every
    /// 10 s for the path). Logged when either changes.
    /// </summary>
    private void UseAircraft(string? title = null, string? path = null)
    {
        if ((title is null || title == _aircraftTitle) && (path is null || path == _aircraftPath))
            return;
        _aircraftTitle = title ?? _aircraftTitle;
        _aircraftPath = path ?? _aircraftPath;
        bool pmdg = Pmdg737.IsPmdg737(_aircraftPath) || Pmdg737.IsPmdg737(_aircraftTitle);
        _log.LogInformation("Aircraft: {Title} ({Path}){Pmdg}.", _aircraftTitle, _aircraftPath,
            pmdg ? ", a PMDG 737: its radios are worked through PMDG's own events" : "");
        _recorder.Record("msfs", "aircraft", w =>
        {
            w.WriteString("title", _aircraftTitle);
            w.WriteString("path", _aircraftPath);
            w.WriteBoolean("pmdg737", pmdg);
        });
        if (pmdg == _pmdg737)
            return;
        _pmdg737 = pmdg;
        Changed?.Invoke();
    }

    // The SimConnect thread's view of the loaded aircraft.
    private string? _aircraftTitle;
    private string? _aircraftPath;

    /// <summary>Takes a new listing of the aircraft's GNS keys; says so when the units found change (another aircraft).</summary>
    private void UseGns(GnsInputEvents gns, int inputEvents)
    {
        var before = _gns;
        _gns = gns; // fresh hashes even for the same aircraft
        if (before.Units.SequenceEqual(gns.Units, StringComparer.OrdinalIgnoreCase))
            return;
        if (gns.Units.Count > 0)
            _log.LogInformation("This aircraft's GNS keys can be pressed: {Units} (of {Count} input events).", string.Join(", ", gns.Units), inputEvents);
        else
            _log.LogInformation("This aircraft has no stock GNS 430/530 input events: ADF and DME stay radios.");
        _recorder.Record("msfs", "gns", w =>
        {
            w.WriteNumber("inputEvents", inputEvents);
            w.WriteStartArray("units");
            foreach (var unit in gns.Units)
                w.WriteStringValue(unit);
            w.WriteEndArray();
        });
        Changed?.Invoke();
    }

    /// <summary>Records the SimVars as MSFS sent them, by name, before any conversion.</summary>
    private void RecordValues((string Name, string Unit)[] variables, double[] values)
    {
        _recorder.Record("msfs", "values", w =>
        {
            w.WriteStartObject("values");
            for (int i = 0; i < variables.Length; i++)
                w.WriteNumberOrNull(variables[i].Name, values[i]);
            w.WriteEndObject();
        });
    }

    internal static RadioState ToState(double[] v) => new()
    {
        Com1 = new RadioPair(Frequencies.ComChannelKhz(v[0]), Frequencies.ComChannelKhz(v[1])),
        Com2 = new RadioPair(Frequencies.ComChannelKhz(v[2]), Frequencies.ComChannelKhz(v[3])),
        Nav1 = new RadioPair(Frequencies.NavKhz(v[4]), Frequencies.NavKhz(v[5])),
        Nav2 = new RadioPair(Frequencies.NavKhz(v[6]), Frequencies.NavKhz(v[7])),
        Adf1Khz = (int)Math.Round(v[8]),
        Adf2Khz = (int)Math.Round(v[9]),
        Dme1Nm = v[12] != 0 ? v[10] : double.NaN,
        Dme2Nm = v[13] != 0 ? v[11] : double.NaN,
        Squawk = Squawk.FromBco16((uint)v[14]),
        BaroHpa = v[15],
    };

    internal static GpsNav ToGps(double[] v) => new(v[0], v[1], v[2] != 0);

    /// <summary>Packed BCD, one decimal digit per nibble: 362000 -> 0x00362000.</summary>
    internal static uint ToBcd(uint value)
    {
        uint result = 0;
        for (int shift = 0; shift < 32 && value > 0; shift += 4, value /= 10)
            result |= value % 10 << shift;
        return result;
    }

    private void SetDisconnected()
    {
        while (_outbox.TryDequeue(out _)) { }
        while (_inputs.TryDequeue(out _)) { }
        while (_pmdgOutbox.TryDequeue(out _)) { }
        _gns = GnsInputEvents.None;
        _pmdg737 = false;
        _state = RadioState.Empty;
        if (!_connected)
            return;
        _connected = false;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _cts.Dispose();
    }
}
