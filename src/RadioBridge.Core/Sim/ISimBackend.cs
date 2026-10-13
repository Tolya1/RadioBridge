namespace RadioBridge.Core.Sim;

/// <summary>
/// A problem that keeps a simulator from connecting and that the user can act on, shown in the window.
/// Not having started the simulator yet is not a problem and never produces one.
/// </summary>
/// <param name="CanLocateSimConnect">The fix is to point RadioBridge at a SimConnect library.</param>
public sealed record SimNotice(string Text, bool CanLocateSimConnect = false);

/// <summary>A connection to one simulator. Implementations reconnect on their own.</summary>
public interface ISimBackend : IDisposable
{
    /// <summary>Short name for the UI, e.g. "MSFS" or "X-Plane".</summary>
    string Name { get; }

    bool IsConnected { get; }

    /// <summary>Latest values reported by the sim (<see cref="RadioState.Empty"/> when disconnected).</summary>
    RadioState State { get; }

    /// <summary>Why this simulator can't connect, or null. Changes raise <see cref="Changed"/>.</summary>
    SimNotice? Notice { get; }

    /// <summary>
    /// Whether this simulator takes <see cref="PressGpsKey"/>. While the connected one doesn't, ADF and DME stay
    /// radios even with GPS control switched on.
    /// </summary>
    bool SupportsGpsKeys => false;

    /// <summary>
    /// Whether the loaded aircraft's radios can only be turned, not set (the PMDG 737): then the panel sends
    /// <see cref="TurnKnobs"/> instead of SetStandby, SetAdf, SetSquawk and SetBaro.
    /// </summary>
    bool TurnsKnobs => false;

    /// <summary>Raised from a background thread when the connection, <see cref="State"/> or <see cref="Notice"/> changes.</summary>
    event Action? Changed;

    void Start();

    /// <summary>Queues a command; dropped when not connected.</summary>
    void Send(SimCommand command);
}
