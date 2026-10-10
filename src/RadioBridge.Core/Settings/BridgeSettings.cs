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

    public AccelerationSettings Acceleration { get; init; } = new();

    public int LongPressMs { get; init; } = 600;

    /// <summary>Most time between ACT/STBY presses that still count as one quick series (triple press).</summary>
    public int MultiPressGapMs { get; init; } = 500;

    /// <summary>Drive the panel with built-in test radios while no simulator is connected.</summary>
    public bool TestMode { get; init; }

    /// <summary>
    /// Closing the window keeps RadioBridge running in the tray. Off by default: Windows 11 hides new
    /// tray icons in the overflow, so a window closed to the tray looks like a vanished app.
    /// </summary>
    public bool CloseToTray { get; init; }

    /// <summary>Start hidden in the tray (with <see cref="CloseToTray"/>) or minimized.</summary>
    public bool StartInTray { get; init; }

    // Read at startup.
    public bool MsfsEnabled { get; init; } = true;
    public bool XPlaneEnabled { get; init; } = true;
    public int XPlanePort { get; init; } = 8086;
}

public static class AppPaths
{
    /// <summary>%APPDATA%\RadioBridge on Windows, ~/.config/RadioBridge on Linux and macOS.</summary>
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RadioBridge");
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
