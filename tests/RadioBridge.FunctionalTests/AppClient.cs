using System.Net.Http.Json;
using RadioBridge.Core.Automation;
using RadioBridge.Core.Panel;
using RadioBridge.Core.Settings;
using RadioBridge.Core.Sim;

namespace RadioBridge.FunctionalTests;

public sealed record PanelInfo(bool Connected, Selector? Upper, Selector? Lower);

public sealed record SimInfo(bool Connected, string Name, RadioState State);

/// <summary>GET /api/state.</summary>
public sealed record AppState(PanelInfo Panel, PanelDisplays Displays, string DisplayReport, SimInfo Sim, BridgeSettings Settings);

/// <summary>
/// Drives the running app through its automation API. Panel input is sent as raw 24-bit reports,
/// built here from the panel's documented bit layout (independently of the app's decoder): the
/// client keeps the "physical" state (selector positions, held buttons) and pulses knob bits.
/// </summary>
public sealed class AppClient(HttpClient http)
{
    private const int UpperButtonBit = 14;
    private const int LowerButtonBit = 15;
    private const int UpperKnobBit = 16;
    private const int LowerKnobBit = 20;

    private int _bits;

    public async Task<AppState?> TryStateAsync()
    {
        try
        {
            return await http.GetFromJsonAsync<AppState>("state", AutomationServer.Json);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    public async Task<AppState> StateAsync() =>
        await http.GetFromJsonAsync<AppState>("state", AutomationServer.Json) ?? throw new InvalidOperationException("Empty state.");

    public async Task PutSettingsAsync(BridgeSettings settings)
    {
        using var response = await http.PutAsJsonAsync("settings", settings, AutomationServer.Json);
        response.EnsureSuccessStatusCode();
    }

    public async Task ShutdownAsync()
    {
        using var response = await http.PostAsync("shutdown", null);
    }

    public async Task SelectAsync(PanelRow row, Selector selector)
    {
        int shift = row == PanelRow.Upper ? 0 : 7;
        _bits = (_bits & ~(0x7F << shift)) | (1 << (shift + (int)selector));
        await SendAsync(_bits);
    }

    /// <summary>Turns a knob by whole detents; slow by default so acceleration does not kick in.</summary>
    public async Task TurnAsync(PanelRow row, Knob knob, int direction, int detents = 1, int intervalMs = 150)
    {
        int bit = (row == PanelRow.Upper ? UpperKnobBit : LowerKnobBit) + (knob == Knob.Inner ? 0 : 2) + (direction > 0 ? 0 : 1);
        for (int i = 0; i < detents; i++)
        {
            await SendAsync(_bits | (1 << bit));
            await SendAsync(_bits);
            if (i < detents - 1)
                await Task.Delay(intervalMs);
        }
    }

    public async Task PressAsync(PanelRow row, int holdMs = 80)
    {
        int bit = 1 << (row == PanelRow.Upper ? UpperButtonBit : LowerButtonBit);
        await SendAsync(_bits | bit);
        await Task.Delay(holdMs);
        await SendAsync(_bits);
    }

    /// <summary>Polls the app until <paramref name="condition"/> holds; fails with the last state otherwise.</summary>
    public async Task<AppState> WaitForAsync(Func<AppState, bool> condition, string what, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var state = await StateAsync();
            if (condition(state))
                return state;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for {what}.\nDisplays: {state.Displays}\nSim: {state.Sim}");
            await Task.Delay(50);
        }
    }

    private async Task SendAsync(int bits)
    {
        using var response = await http.PostAsJsonAsync("panel/report", new { bits });
        response.EnsureSuccessStatusCode();
    }
}
