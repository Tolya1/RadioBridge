namespace RadioBridge.Core.Sim.XPlane;

/// <summary>
/// Bookkeeping for one X-Plane connection: the requests in flight and how long X-Plane takes to answer them,
/// knob writes coalesced per dataref, and the values RadioBridge has set that X-Plane hasn't answered yet.
/// <para>
/// Normally X-Plane answers within a frame or two and none of this shows. But an X-Plane can fall far behind
/// on a connection (one was seen answering 30 s late, in order). Then, without this, every knob change springs
/// back once the panel's short hold-off ends, and half a minute later X-Plane replays each detent, so the
/// values seem to change by themselves. With it, a spin sends a few values instead of one per detent, the
/// panel keeps showing what was set until X-Plane answers, and <see cref="SlowSeconds"/> tells the user.
/// </para>
/// All members are thread-safe.
/// </summary>
internal sealed class XPlaneRequests(TimeProvider? time = null)
{
    /// <summary>Writes to the same dataref closer together than this are merged: only the newest value goes out.</summary>
    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How long a value is still laid over X-Plane's after X-Plane answered: the answer comes as the request is
    /// handled, and the value (or a flip, which happens when the 150 ms press ends) reaches us a few frames later.
    /// </summary>
    public static readonly TimeSpan AnswerGrace = TimeSpan.FromMilliseconds(500);

    /// <summary>Answers slower than this make X-Plane "slow", which the window says.</summary>
    public static readonly TimeSpan SlowAfter = TimeSpan.FromSeconds(2);

    /// <summary>A slow answer is remembered this long, so the notice doesn't flicker between knob turns.</summary>
    public static readonly TimeSpan SlowMemory = TimeSpan.FromSeconds(10);

    /// <summary>A request X-Plane never answers is forgotten after this.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(2);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<int, long> _sentAt = new();
    private readonly List<Expectation> _expected = new();
    private readonly Dictionary<string, long> _lastWrite = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (double Value, Expectation Expectation)> _queued = new(StringComparer.Ordinal);
    private (TimeSpan Lag, long At)? _lastAnswer;

    /// <summary>
    /// Plans a write. Null: send it now (then call <see cref="Sent"/>). Otherwise it is held back, because the
    /// same dataref was written a moment ago: the newest value goes out after the returned delay
    /// (<see cref="TimeSpan.Zero"/> when a flush is already scheduled), through <see cref="TakeQueued"/>.
    /// </summary>
    public TimeSpan? PlanWrite(string dataref, double value, Func<RadioState, RadioState> apply)
    {
        lock (_gate)
        {
            long now = _time.GetTimestamp();
            if (_queued.TryGetValue(dataref, out var queued))
            {
                queued.Expectation.Apply = apply;
                _queued[dataref] = (value, queued.Expectation);
                return TimeSpan.Zero;
            }
            if (_lastWrite.TryGetValue(dataref, out long last) && _time.GetElapsedTime(last, now) < CoalesceWindow)
            {
                var expectation = new Expectation(apply, now);
                _expected.Add(expectation);
                _queued[dataref] = (value, expectation);
                return CoalesceWindow - _time.GetElapsedTime(last, now);
            }
            return null;
        }
    }

    /// <summary>The newest held-back value for <paramref name="dataref"/>, to send now; null if there is none.</summary>
    public (double Value, Expectation Expectation)? TakeQueued(string dataref)
    {
        lock (_gate)
            return _queued.Remove(dataref, out var queued) ? queued : null;
    }

    /// <summary>
    /// A request went out. <paramref name="apply"/> lays its effect over X-Plane's values until it is answered;
    /// <paramref name="expectation"/> is the held-back write being sent now, if it is one.
    /// </summary>
    public void Sent(int request, string? dataref = null, Func<RadioState, RadioState>? apply = null, Expectation? expectation = null)
    {
        lock (_gate)
        {
            long now = _time.GetTimestamp();
            _sentAt[request] = now;
            if (dataref is not null)
                _lastWrite[dataref] = now;
            if (expectation is not null)
                expectation.Request = request;
            else if (apply is not null)
                _expected.Add(new Expectation(apply, now) { Request = request });
        }
    }

    /// <summary>X-Plane answered <paramref name="request"/>; returns how long that took (null for a request not sent here).</summary>
    public TimeSpan? Answered(int request, bool success)
    {
        lock (_gate)
        {
            long now = _time.GetTimestamp();
            if (!_sentAt.Remove(request, out long sentAt))
                return null;
            var lag = _time.GetElapsedTime(sentAt, now);
            _lastAnswer = (lag, now);
            foreach (var expectation in _expected.Where(e => e.Request == request).ToList())
            {
                if (success)
                    expectation.AnsweredAt = now;
                else
                    _expected.Remove(expectation); // refused: X-Plane keeps its value
            }
            return lag;
        }
    }

    /// <summary>X-Plane's values with everything RadioBridge set and X-Plane hasn't answered (or only just answered) laid over them.</summary>
    public RadioState Apply(RadioState xplane)
    {
        lock (_gate)
        {
            long now = _time.GetTimestamp();
            _expected.RemoveAll(e => e.AnsweredAt is long answered
                ? _time.GetElapsedTime(answered, now) > AnswerGrace
                : _time.GetElapsedTime(e.Created, now) > MaxWait);
            var state = xplane;
            foreach (var expectation in _expected)
                state = expectation.Apply(state);
            return state;
        }
    }

    /// <summary>
    /// How late X-Plane is answering, in seconds, when that is <see cref="SlowAfter"/> or more: the oldest
    /// unanswered request's age, or a slow answer within the last <see cref="SlowMemory"/>. Null when it keeps up.
    /// </summary>
    public double? SlowSeconds()
    {
        lock (_gate)
        {
            long now = _time.GetTimestamp();
            foreach (var (request, sentAt) in _sentAt.ToList())
            {
                if (_time.GetElapsedTime(sentAt, now) > MaxWait)
                    _sentAt.Remove(request);
            }
            var waiting = _sentAt.Count == 0 ? TimeSpan.Zero : _time.GetElapsedTime(_sentAt.Values.Min(), now);
            // The last answer's delay is the best estimate while requests are still out, however long ago it came.
            var recent = _lastAnswer is { } last && (_sentAt.Count > 0 || _time.GetElapsedTime(last.At, now) < SlowMemory) ? last.Lag : TimeSpan.Zero;
            // A fast answer since means X-Plane has caught up, whatever the last slow one was.
            var lag = waiting > recent ? waiting : recent;
            return lag >= SlowAfter ? lag.TotalSeconds : null;
        }
    }

    /// <summary>Something RadioBridge set, laid over X-Plane's values until X-Plane has answered it.</summary>
    internal sealed class Expectation(Func<RadioState, RadioState> apply, long created)
    {
        public Func<RadioState, RadioState> Apply { get; set; } = apply;
        public long Created { get; } = created;

        /// <summary>The request that carries it; null while it is held back.</summary>
        public int? Request { get; set; }

        public long? AnsweredAt { get; set; }
    }
}
