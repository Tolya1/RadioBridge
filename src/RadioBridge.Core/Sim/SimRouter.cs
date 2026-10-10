using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RadioBridge.Core.Sim;

/// <summary>
/// Runs every backend and follows whichever simulator is connected. The first sim to connect stays
/// active until it disconnects; then any other connected sim takes over.
/// </summary>
public sealed class SimRouter : ISimBackend
{
    private readonly IReadOnlyList<ISimBackend> _backends;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private ISimBackend? _active;

    public SimRouter(IEnumerable<ISimBackend> backends, ILogger<SimRouter>? log = null)
    {
        _backends = backends.ToList();
        _log = log ?? NullLogger<SimRouter>.Instance;
        foreach (var backend in _backends)
            backend.Changed += () => OnBackendChanged(backend);
    }

    public ISimBackend? Active
    {
        get { lock (_gate) return _active; }
    }

    public string Name => Active?.Name ?? "";
    public bool IsConnected => Active is not null;
    public RadioState State => Active?.State ?? RadioState.Empty;

    public event Action? Changed;

    public void Start()
    {
        foreach (var backend in _backends)
            backend.Start();
    }

    public void Send(SimCommand command) => Active?.Send(command);

    private void OnBackendChanged(ISimBackend backend)
    {
        ISimBackend? before, after;
        lock (_gate)
        {
            before = _active;
            if (_active is null && backend.IsConnected)
                _active = backend;
            else if (_active == backend && !backend.IsConnected)
                _active = _backends.FirstOrDefault(b => b != backend && b.IsConnected);
            after = _active;
        }

        if (before != after)
            _log.LogInformation("Active simulator: {Sim}.", after?.Name ?? "none");

        // Other sims' value updates are irrelevant while they are not the active one.
        if (before != after || backend == after)
            Changed?.Invoke();
    }

    public void Dispose()
    {
        foreach (var backend in _backends)
            backend.Dispose();
    }
}
