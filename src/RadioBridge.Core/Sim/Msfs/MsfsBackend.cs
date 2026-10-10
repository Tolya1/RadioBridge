using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly ConcurrentQueue<(Ev Event, uint Data)> _outbox = new();
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;
    private volatile bool _connected;
    private volatile RadioState _state = RadioState.Empty;

    public MsfsBackend(ILogger<MsfsBackend>? log = null) => _log = log ?? NullLogger<MsfsBackend>.Instance;

    public string Name => "MSFS";
    public bool IsConnected => _connected;
    public RadioState State => _state;

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
        if (ResolvedPath is null)
        {
            _log.LogWarning("SimConnect.dll not found, so MSFS is disabled. Searched: {Paths}",
                string.Join("; ", SimConnectLocator.Candidates()));
            return;
        }

        _log.LogInformation("Using {Path}.", ResolvedPath);
        _thread = new Thread(Run) { IsBackground = true, Name = "SimConnect" };
        _thread.Start();
    }

    public void Send(SimCommand command)
    {
        if (!_connected)
            return;

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
                _log.LogError(ex, "SimConnect.dll at {Path} could not be used (it must be the 64-bit build). MSFS is disabled.", ResolvedPath);
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

        foreach (var (ev, name) in EventNames)
            MapClientEventToSimEvent(handle, (uint)ev, name);

        RequestDataOnSimObject(handle, RequestRadios, DefineRadios, ObjectIdUser, PeriodSimFrame, DataRequestFlagChanged, 0, 0, 0);
    }

    private void Pump(IntPtr handle, CancellationToken ct)
    {
        var values = new double[Variables.Length];
        bool open = true;
        while (open && !ct.IsCancellationRequested)
        {
            while (GetNextDispatch(handle, out IntPtr data, out _) == 0 && data != IntPtr.Zero)
            {
                switch ((uint)Marshal.ReadInt32(data, RecvIdOffset))
                {
                    case RecvOpen:
                        _connected = true;
                        _log.LogInformation("Connected to MSFS.");
                        Changed?.Invoke();
                        break;

                    case RecvQuit:
                        _log.LogInformation("MSFS closed the connection.");
                        open = false;
                        break;

                    case RecvException:
                        _log.LogDebug("SimConnect exception {Code}.", Marshal.ReadInt32(data, ExceptionCodeOffset));
                        break;

                    case RecvSimObjectData when (uint)Marshal.ReadInt32(data, SimObjectRequestIdOffset) == RequestRadios:
                        Marshal.Copy(IntPtr.Add(data, SimObjectDataOffset), values, 0, values.Length);
                        _state = ToState(values);
                        Changed?.Invoke();
                        break;
                }

                if (!open)
                    break;
            }

            while (_outbox.TryDequeue(out var message))
                TransmitClientEvent(handle, ObjectIdUser, (uint)message.Event, message.Data, GroupPriorityHighest, EventFlagGroupIdIsPriority);

            ct.WaitHandle.WaitOne(15);
        }
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
