using RadioBridge.Core.Panel;

namespace RadioBridge.Core.Radio;

/// <summary>
/// Conservative by default: a normal turn (detents 40 ms or more apart) is never accelerated, and
/// even a fast flick only triples the step.
/// </summary>
public sealed record AccelerationSettings
{
    public bool Enabled { get; init; } = true;

    /// <summary>Detents closer together than this count as a fast spin.</summary>
    public int FastDetentMs { get; init; } = 20;

    /// <summary>Detents closer together than this count as a medium spin.</summary>
    public int MediumDetentMs { get; init; } = 40;

    public int FastMultiplier { get; init; } = 3;
    public int MediumMultiplier { get; init; } = 2;
}

/// <summary>Turns detent timing into a step multiplier, so a quick spin covers a big range.</summary>
public sealed class KnobAccelerator(TimeProvider time)
{
    private readonly Dictionary<(PanelRow, Knob), (long At, int Direction)> _last = new();

    public int Multiplier(KnobTurned turn, AccelerationSettings settings)
    {
        long now = time.GetTimestamp();
        var key = (turn.Row, turn.Knob);
        bool hasLast = _last.TryGetValue(key, out var last);
        _last[key] = (now, turn.Direction);

        // Reversing direction is a deliberate correction: always a single step.
        if (!settings.Enabled || !hasLast || last.Direction != turn.Direction)
            return 1;

        double ms = time.GetElapsedTime(last.At, now).TotalMilliseconds;
        if (ms < settings.FastDetentMs)
            return Math.Max(1, settings.FastMultiplier);
        if (ms < settings.MediumDetentMs)
            return Math.Max(1, settings.MediumMultiplier);
        return 1;
    }
}
