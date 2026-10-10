namespace RadioBridge.Core.Panel;

/// <summary>
/// Turns successive input reports into events. Stateful: selectors and buttons are reported on
/// change, knob detents on the rising edge of their bit. The first report only establishes the
/// selector positions.
/// </summary>
public sealed class RadioPanelDecoder
{
    private static readonly PanelRow[] Rows = [PanelRow.Upper, PanelRow.Lower];
    private static readonly Knob[] Knobs = [Knob.Inner, Knob.Outer];

    private int? _previous;
    private readonly Selector?[] _selectors = new Selector?[2];

    public void Reset()
    {
        _previous = null;
        Array.Clear(_selectors);
    }

    public IReadOnlyList<PanelEvent> Decode(ReadOnlySpan<byte> report)
    {
        if (report.Length < RadioPanelProtocol.InputReportLength)
            return [];

        int state = RadioPanelProtocol.ReadState(report);
        var events = new List<PanelEvent>();

        foreach (var row in Rows)
        {
            var position = RadioPanelProtocol.SelectorOf(state, row);
            if (position is not null && position != _selectors[(int)row])
            {
                _selectors[(int)row] = position;
                events.Add(new SelectorMoved(row, position.Value));
            }
        }

        if (_previous is int previous)
        {
            foreach (var row in Rows)
            {
                bool pressed = RadioPanelProtocol.ButtonOf(state, row);
                if (pressed != RadioPanelProtocol.ButtonOf(previous, row))
                    events.Add(new ButtonChanged(row, pressed));

                foreach (var knob in Knobs)
                {
                    foreach (int direction in (ReadOnlySpan<int>)[1, -1])
                    {
                        int mask = 1 << RadioPanelProtocol.KnobBit(row, knob, direction);
                        if ((state & mask) != 0 && (previous & mask) == 0)
                            events.Add(new KnobTurned(row, knob, direction));
                    }
                }
            }
        }

        _previous = state;
        return events;
    }
}
