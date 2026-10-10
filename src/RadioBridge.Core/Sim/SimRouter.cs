using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RadioBridge.Core.Sim;

/// <summary>
/// Runs every backend and follows whichever simulator is connected. The first real simulator to
/// connect stays active until it disconnects; then any other connected one takes over. The optional
/// fallback (test mode) is only used while no real simulator is connected.
/// </summary>
public sealed class SimRouter : ISimBackend
{
    private readonly IReadOnlyList<ISimBackend> _backends;
    private readonly ISimBackend? _fallback;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private ISimBackend? _active;

    public SimRouter(IEnumerable<ISimBackend> backends, ISimBackend? fallback = null, ILogger<SimRouter>? log = null)
    {
        _backends = backends.ToList();
        _fallback = fallback;
        _log = log ?? NullLogger<SimRouter>.Instance;
        foreach (var backend in All)
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

    private IEnumerable<ISimBackend> All => _fallback is null ? _backends : _backends.Append(_fallback);

    public void Start()
    {
        foreach (var backend in All)
            backend.Start();
    }

    public void Send(SimCommand command) => Active?.Send(command);

    private void OnBackendChanged(ISimBackend backend)
    {
        ISimBackend? before, after;
        lock (_gate)
        {
            before = _active;
            _active = after = Choose();
        }

        if (before != after)
            _log.LogInformation("Active simulator: {Sim}.", after?.Name ?? "none");

        // Other sims' value updates are irrelevant while they are not the active one.
        if (before != after || backend == after)
            Changed?.Invoke();
    }

    private ISimBackend? Choose()
    {
        if (_active is { IsConnected: true } current && current != _fallback)
            return current;
        return _backends.FirstOrDefault(b => b.IsConnected)
               ?? (_fallback is { IsConnected: true } ? _fallback : null);
    }

    public void Dispose()
    {
        foreach (var backend in All)
            backend.Dispose();
    }
}
