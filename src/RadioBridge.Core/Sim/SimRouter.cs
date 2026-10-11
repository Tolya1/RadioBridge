using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RadioBridge.Core.Diagnostics;

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
    private readonly Recorder _recorder;
    private readonly object _gate = new();
    private ISimBackend? _active;
    private SimNotice? _lastNotice;
    private RadioState? _recordedState;

    public SimRouter(IEnumerable<ISimBackend> backends, ISimBackend? fallback = null, ILogger<SimRouter>? log = null, Recorder? recorder = null)
    {
        _backends = backends.ToList();
        _fallback = fallback;
        _log = log ?? NullLogger<SimRouter>.Instance;
        _recorder = recorder ?? new Recorder();
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

    /// <summary>
    /// While a real simulator is connected, its own problem if it has one (X-Plane answering late). Otherwise
    /// what keeps the real simulators from connecting (test mode doesn't count).
    /// </summary>
    public SimNotice? Notice
    {
        get
        {
            if (Active is { } active && active != _fallback)
                return active.Notice;
            if (_backends.Any(b => b.IsConnected))
                return null;
            var notices = _backends.Select(b => b.Notice).OfType<SimNotice>().ToList();
            return notices.Count == 0
                ? null
                : new SimNotice(string.Join(" ", notices.Select(n => n.Text)), notices.Any(n => n.CanLocateSimConnect));
        }
    }

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
        bool noticeChanged;
        lock (_gate)
        {
            before = _active;
            _active = after = Choose();
            var notice = Notice;
            noticeChanged = notice != _lastNotice;
            _lastNotice = notice;
            RecordLocked(before, after, noticeChanged ? notice : null);
        }

        if (before != after)
            _log.LogInformation("Active simulator: {Sim}.", after?.Name ?? "none");

        // Other sims' value updates are irrelevant while they are not the active one.
        if (before != after || backend == after || noticeChanged)
            Changed?.Invoke();
    }

    /// <summary>
    /// Records which simulator is followed and the radio values as RadioBridge understands them (after converting
    /// from the sim's units), whenever they change.
    /// </summary>
    private void RecordLocked(ISimBackend? before, ISimBackend? after, SimNotice? newNotice)
    {
        if (!_recorder.IsRecording)
            return;
        if (before != after)
            _recorder.Record("sim", "active", w => w.WriteString("name", after?.Name));
        if (newNotice is not null)
            _recorder.Record("sim", "notice", w => w.WriteString("text", newNotice.Text));
        var state = after?.State ?? RadioState.Empty;
        if (state != _recordedState)
        {
            _recordedState = state;
            _recorder.Record("sim", "state", w => w.WriteRadioState("radios", state));
        }
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
