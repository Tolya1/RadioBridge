using RadioBridge.Core.Panel;
using RadioBridge.Core.Settings;
using RadioBridge.Core.Sim;

namespace RadioBridge.Tests;

internal sealed class FakePanel : IRadioPanel
{
    public bool IsConnected { get; set; } = true;
    public PanelDisplays Shown { get; private set; } = PanelDisplays.Blank;

    public event Action<PanelEvent>? Input;
    public event Action<bool>? ConnectionChanged;

    public void Start() => ConnectionChanged?.Invoke(IsConnected);
    public void Show(PanelDisplays displays) => Shown = displays;
    public void Raise(PanelEvent input) => Input?.Invoke(input);
    public void Dispose() { }
}

internal sealed class FakeSim(string name = "FakeSim") : ISimBackend
{
    public string Name { get; } = name;
    public bool IsConnected { get; private set; }
    public RadioState State { get; private set; } = RadioState.Empty;
    public List<SimCommand> Sent { get; } = [];

    public event Action? Changed;

    public void Start() { }
    public void Send(SimCommand command) => Sent.Add(command);

    public void Connect(RadioState state)
    {
        IsConnected = true;
        State = state;
        Changed?.Invoke();
    }

    public void Report(RadioState state)
    {
        State = state;
        Changed?.Invoke();
    }

    public void Disconnect()
    {
        IsConnected = false;
        State = RadioState.Empty;
        Changed?.Invoke();
    }

    public void Dispose() { }
}

/// <summary>A settings file in a private temp folder, deleted with the test.</summary>
internal sealed class TempSettings : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RadioBridgeTests", Guid.NewGuid().ToString("N"));

    public TempSettings() => Store = new SettingsStore(Path.Combine(_dir, "settings.json"));

    public SettingsStore Store { get; }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}

internal static class Wait
{
    public static async Task Until(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(20);
        }
    }
}
