namespace RadioBridge.Core.Panel;

/// <summary>
/// A software radio panel for automation and functional tests. Raw input reports are injected
/// instead of read from USB and go through the same decoder; the display report that a physical
/// panel would receive is kept for inspection.
/// </summary>
public sealed class VirtualRadioPanel : IRadioPanel
{
    private readonly RadioPanelDecoder _decoder = new();
    private readonly object _gate = new();
    private byte[] _displayReport = RadioPanelProtocol.EncodeDisplays(PanelDisplays.Blank);

    public bool IsConnected => true;

    /// <summary>The last 22-byte display report, exactly as it would be sent to the hardware.</summary>
    public byte[] DisplayReport
    {
        get { lock (_gate) return _displayReport; }
    }

    public event Action<PanelEvent>? Input;
    public event Action<bool>? ConnectionChanged;

    public void Start() => ConnectionChanged?.Invoke(true);

    public void Show(PanelDisplays displays)
    {
        var report = RadioPanelProtocol.EncodeDisplays(displays);
        lock (_gate)
            _displayReport = report;
    }

    /// <summary>Feeds one 3-byte input report through the decoder; returns the resulting events.</summary>
    public IReadOnlyList<PanelEvent> Inject(ReadOnlySpan<byte> report)
    {
        IReadOnlyList<PanelEvent> events;
        lock (_gate)
            events = _decoder.Decode(report);
        foreach (var e in events)
            Input?.Invoke(e);
        return events;
    }

    public void Dispose() { }
}

/// <summary>
/// Several panels acting as one: input from any of them, displays shown on all of them. Its
/// connection state is that of the first panel (the physical one when there is one).
/// </summary>
public sealed class PanelGroup : IRadioPanel
{
    private readonly IReadOnlyList<IRadioPanel> _panels;

    public PanelGroup(IReadOnlyList<IRadioPanel> panels)
    {
        if (panels.Count == 0)
            throw new ArgumentException("At least one panel is required.", nameof(panels));
        _panels = panels;
        foreach (var panel in _panels)
        {
            panel.Input += e => Input?.Invoke(e);
            panel.ConnectionChanged += _ => ConnectionChanged?.Invoke(IsConnected);
        }
    }

    public bool IsConnected => _panels[0].IsConnected;

    public event Action<PanelEvent>? Input;
    public event Action<bool>? ConnectionChanged;

    public void Start()
    {
        foreach (var panel in _panels)
            panel.Start();
    }

    public void Show(PanelDisplays displays)
    {
        foreach (var panel in _panels)
            panel.Show(displays);
    }

    public void Dispose()
    {
        foreach (var panel in _panels)
            panel.Dispose();
    }
}
