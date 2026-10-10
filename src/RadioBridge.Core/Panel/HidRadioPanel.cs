using System.Diagnostics;
using HidSharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RadioBridge.Core.Panel;

public interface IRadioPanel : IDisposable
{
    bool IsConnected { get; }

    event Action<PanelEvent>? Input;
    event Action<bool>? ConnectionChanged;

    void Start();

    /// <summary>Shows text on the four displays. Cheap to call often: unchanged output is not resent.</summary>
    void Show(PanelDisplays displays);
}

/// <summary>
/// The physical panel over USB HID (HidSharp, so it runs on Windows, Linux and macOS). A background
/// thread opens the first matching device, reads input reports and reconnects after unplugging.
/// <para>
/// The device is opened shared, so other panel software (e.g. SPAD.neXt or HangarControl driving
/// other panels) can keep running. The displays are re-sent every few seconds, so whatever another
/// program writes to this panel (e.g. a profile switch blanking all panels) is undone quickly.
/// </para>
/// </summary>
public sealed class HidRadioPanel : IRadioPanel
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DisplayRefresh = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Never lock other programs out of the panel: no HidSharp exclusivity layer, and the OS handle is
    /// shared for reading and writing. Other panel software may fight over the displays, but it can
    /// always open the device.
    /// </summary>
    internal static readonly OpenConfiguration SharedAccess = CreateSharedAccess();

    private static OpenConfiguration CreateSharedAccess()
    {
        var config = new OpenConfiguration();
        config.SetOption(OpenOption.Exclusive, false);
        config.SetOption(OpenOption.Interruptible, false);
        return config;
    }

    private readonly ILogger _log;
    private readonly RadioPanelDecoder _decoder = new();
    private readonly object _writeGate = new();
    private readonly CancellationTokenSource _cts = new();

    private Thread? _thread;
    private HidStream? _stream;
    private int _featureLength;
    private byte[] _wanted = RadioPanelProtocol.EncodeDisplays(PanelDisplays.Blank);
    private byte[]? _written;
    private long _writtenAt;
    private volatile bool _connected;
    private bool _loggedMissing;

    public HidRadioPanel(ILogger<HidRadioPanel>? log = null) => _log = log ?? NullLogger<HidRadioPanel>.Instance;

    public bool IsConnected => _connected;

    public event Action<PanelEvent>? Input;
    public event Action<bool>? ConnectionChanged;

    public void Start()
    {
        if (_thread is not null)
            return;
        _thread = new Thread(Run) { IsBackground = true, Name = "RadioPanel HID" };
        _thread.Start();
    }

    public void Show(PanelDisplays displays)
    {
        var report = RadioPanelProtocol.EncodeDisplays(displays);
        lock (_writeGate)
        {
            _wanted = report;
            WriteLocked();
        }
    }

    private void Run()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            var device = DeviceList.Local.GetHidDevices(RadioPanelProtocol.VendorId, RadioPanelProtocol.ProductId).FirstOrDefault();
            if (device is null)
            {
                if (!_loggedMissing)
                    _log.LogInformation("Radio panel not found; waiting for it to be plugged in.");
                _loggedMissing = true;
                ct.WaitHandle.WaitOne(RetryDelay);
                continue;
            }

            if (!device.TryOpen(SharedAccess, out HidStream stream))
            {
                _log.LogWarning("Radio panel found but could not be opened ({Path}). On Linux, install the udev rule from packaging/linux.", device.DevicePath);
                ct.WaitHandle.WaitOne(RetryDelay * 5);
                continue;
            }

            _loggedMissing = false;
            try
            {
                using (stream)
                {
                    stream.ReadTimeout = 500;
                    lock (_writeGate)
                    {
                        _stream = stream;
                        _featureLength = device.GetMaxFeatureReportLength();
                        _written = null; // force a full refresh on (re)connect
                        WriteLocked();
                    }
                    _decoder.Reset();
                    SetConnected(true);

                    // The panel reports only on change: ask for the current selector positions.
                    if (CurrentInputReport.TryRead(device, stream) is { } current)
                    {
                        foreach (var ev in _decoder.Decode(current))
                            Input?.Invoke(ev);
                    }
                    else
                    {
                        _log.LogInformation("Could not read the panel's current state; selector positions show after the first move.");
                    }

                    ReadLoop(stream, device.GetMaxInputReportLength(), ct);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogInformation("Radio panel disconnected: {Message}", ex.Message);
            }
            finally
            {
                lock (_writeGate)
                    _stream = null;
                SetConnected(false);
            }

            ct.WaitHandle.WaitOne(RetryDelay);
        }
    }

    private void ReadLoop(HidStream stream, int maxInputLength, CancellationToken ct)
    {
        var buffer = new byte[Math.Max(maxInputLength, RadioPanelProtocol.InputReportLength + 1)];
        while (!ct.IsCancellationRequested)
        {
            RefreshIfStale();

            int count;
            try
            {
                count = stream.Read(buffer, 0, buffer.Length);
            }
            catch (TimeoutException)
            {
                continue;
            }

            // HidSharp prefixes the report id (0 for this device) when the report is one byte longer.
            int offset = count > RadioPanelProtocol.InputReportLength ? 1 : 0;
            if (count - offset < RadioPanelProtocol.InputReportLength)
                continue;

            var report = buffer.AsSpan(offset, RadioPanelProtocol.InputReportLength);
            _log.LogTrace("Panel report {B0:X2} {B1:X2} {B2:X2}", report[0], report[1], report[2]);
            foreach (var ev in _decoder.Decode(report))
                Input?.Invoke(ev);
        }
    }

    /// <summary>Re-sends unchanged displays now and then, in case another program wrote to the panel.</summary>
    private void RefreshIfStale()
    {
        lock (_writeGate)
        {
            if (Stopwatch.GetElapsedTime(_writtenAt) < DisplayRefresh)
                return;
            _written = null;
            WriteLocked();
        }
    }

    private void WriteLocked()
    {
        if (_stream is null || (_written is not null && _written.AsSpan().SequenceEqual(_wanted)))
            return;

        // Feature reports carry the report id (0) first; the device declares the total length.
        var feature = new byte[Math.Max(_featureLength, RadioPanelProtocol.DisplayReportLength + 1)];
        _wanted.AsSpan(0, Math.Min(_wanted.Length, feature.Length - 1)).CopyTo(feature.AsSpan(1));
        try
        {
            _stream.SetFeature(feature);
            _written = _wanted;
            _writtenAt = Stopwatch.GetTimestamp();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Writing the panel displays failed.");
        }
    }

    private void SetConnected(bool connected)
    {
        if (_connected == connected)
            return;
        _connected = connected;
        _log.LogInformation("Radio panel {State}.", connected ? "connected" : "disconnected");
        ConnectionChanged?.Invoke(connected);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _cts.Dispose();
    }
}
