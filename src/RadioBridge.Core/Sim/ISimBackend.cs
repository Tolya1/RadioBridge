namespace RadioBridge.Core.Sim;

/// <summary>A connection to one simulator. Implementations reconnect on their own.</summary>
public interface ISimBackend : IDisposable
{
    /// <summary>Short name for the UI, e.g. "MSFS" or "X-Plane".</summary>
    string Name { get; }

    bool IsConnected { get; }

    /// <summary>Latest values reported by the sim (<see cref="RadioState.Empty"/> when disconnected).</summary>
    RadioState State { get; }

    /// <summary>Raised from a background thread when the connection or <see cref="State"/> changes.</summary>
    event Action? Changed;

    void Start();

    /// <summary>Queues a command; dropped when not connected.</summary>
    void Send(SimCommand command);
}
