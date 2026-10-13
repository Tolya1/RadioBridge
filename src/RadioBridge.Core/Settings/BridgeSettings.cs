using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RadioBridge.Core.Radio;

namespace RadioBridge.Core.Settings;

public sealed record BridgeSettings
{
    /// <summary>Inner COM knob step. Long-press ACT/STBY on COM1/COM2 toggles it.</summary>
    public ComSpacing ComSpacing { get; init; } = ComSpacing.Khz833;

    /// <summary>Show COM as "23.405" (leading 1 dropped, three decimals) instead of "123.40".</summary>
    public bool CompactComDisplay { get; init; } = true;

    public BaroUnit BaroUnit { get; init; } = BaroUnit.Hectopascal;

    /// <summary>
    /// Knob acceleration on COM1/COM2 (other modes never accelerate). Named apart from the earlier,
    /// much livelier "Acceleration" setting so that older settings files get the gentler defaults.
    /// </summary>
    public AccelerationSettings ComAcceleration { get; init; } = new();

    public int LongPressMs { get; init; } = 600;

    /// <summary>Squawk set by holding ACT/STBY on XPDR: 7000 (Europe) or 1200 (US).</summary>
    public int VfrSquawk { get; init; } = 7000;

    /// <summary>
    /// ADF and DME drive the GNS 430/530 instead of the radios (X-Plane, and MSFS aircraft with the stock GNS): ADF types (knobs, ENT,
    /// CRSR), DME has the other keys. Off by default.
    /// </summary>
    public bool GpsControl { get; init; }

    /// <summary>The GPS the lower row drives with <see cref="GpsControl"/>: 2, or 1 for aircraft with one GNS. The upper row always drives GPS 1.</summary>
    public int LowerRowGps { get; init; } = 2;

    /// <summary>Open the readme and third-party notices when RadioBridge starts. "Don't show again" clears it.</summary>
    public bool ShowReaderAtStart { get; init; } = true;

    /// <summary>Drive the panel with built-in test radios while no simulator is connected.</summary>
    public bool TestMode { get; init; }

    /// <summary>
    /// Closing the window keeps RadioBridge running in the tray. Off by default: Windows 11 hides new
    /// tray icons in the overflow, so a window closed to the tray looks like a vanished app.
    /// </summary>
    public bool CloseToTray { get; init; }

    /// <summary>Start hidden in the tray (with <see cref="CloseToTray"/>) or minimized.</summary>
    public bool StartInTray { get; init; }

    /// <summary>
    /// Start a debug recording every time RadioBridge starts, for problems that show up right away. Off by
    /// default, so nothing is recorded unless asked for; only the newest recordings are kept.
    /// </summary>
    public bool RecordAtStart { get; init; }

    // Read at startup.
    public bool MsfsEnabled { get; init; } = true;

    /// <summary>
    /// A SimConnect library to use for MSFS (a file, or a folder holding it); null searches for one. Normally
    /// not needed: RadioBridge finds the sim's own library. Picked with "Locate SimConnect.dll" in the window.
    /// </summary>
    public string? SimConnectPath { get; init; }
    public bool XPlaneEnabled { get; init; } = true;
    public int XPlanePort { get; init; } = 8086;
}

public static class AppPaths
{
    /// <summary>%APPDATA%\RadioBridge on Windows, ~/.config/RadioBridge on Linux and macOS.</summary>
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RadioBridge");

    /// <summary>The folder of the executable that was started (for the single-file app, where RadioBridge.exe is).</summary>
    public static string ExecutableDirectory { get; } = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
}

/// <summary>Loads, saves and publishes <see cref="BridgeSettings"/> (JSON).</summary>
public sealed class SettingsStore
{
    // Lenient on read: the file is meant to be hand-editable.
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly ILogger _log;
    private readonly object _gate = new();

    public SettingsStore(string path, ILogger<SettingsStore>? log = null)
    {
        _path = path;
        _log = log ?? NullLogger<SettingsStore>.Instance;
        Current = Load();
    }

    public string FilePath => _path;

    public BridgeSettings Current { get; private set; }

    public event Action<BridgeSettings>? Changed;

    public void Update(Func<BridgeSettings, BridgeSettings> change)
    {
        BridgeSettings updated;
        lock (_gate)
        {
            updated = change(Current);
            if (updated == Current)
                return;
            Current = updated;
            Save(updated);
        }
        Changed?.Invoke(updated);
    }

    private BridgeSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<BridgeSettings>(File.ReadAllText(_path), Json) ?? new();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read {Path}; using defaults.", _path);
        }
        return new();
    }

    private void Save(BridgeSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, Json));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not save {Path}.", _path);
        }
    }
}
