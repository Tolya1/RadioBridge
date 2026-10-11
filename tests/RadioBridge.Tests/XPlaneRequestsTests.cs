using Microsoft.Extensions.Time.Testing;
using RadioBridge.Core.Sim;
using RadioBridge.Core.Sim.XPlane;

namespace RadioBridge.Tests;

public sealed class XPlaneRequestsTests
{
    private const string Standby = "sim/cockpit2/radios/actuators/com1_standby_frequency_hz_833";

    private static readonly RadioState XPlane = new() { Com1 = new RadioPair(118_000, 121_500) };

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-11T10:41:09Z"));
    private readonly XPlaneRequests _requests;
    private int _nextRequest = 1;

    public XPlaneRequestsTests() => _requests = new XPlaneRequests(_time);

    private static Func<RadioState, RadioState> SetStandby(int khz) => s => XPlaneNames.Apply(s, Standby, khz);

    /// <summary>A knob write as the backend makes it: sent at once, or held back.</summary>
    private int? Write(int khz)
    {
        if (_requests.PlanWrite(Standby, khz, SetStandby(khz)) is not null)
            return null;
        int request = _nextRequest++;
        _requests.Sent(request, Standby, SetStandby(khz));
        return request;
    }

    [Fact]
    public void A_fast_spin_sends_the_first_value_and_then_only_the_newest_every_100_ms()
    {
        Assert.NotNull(Write(121_505));                                           // goes out at once
        _time.Advance(TimeSpan.FromMilliseconds(30));
        Assert.Equal(TimeSpan.FromMilliseconds(70), _requests.PlanWrite(Standby, 121_510, SetStandby(121_510))); // held back, flush in 70 ms
        _time.Advance(TimeSpan.FromMilliseconds(30));
        Assert.Equal(TimeSpan.Zero, _requests.PlanWrite(Standby, 121_515, SetStandby(121_515)));                // replaces it; flush already planned

        var queued = _requests.TakeQueued(Standby);
        Assert.Equal(121_515, queued!.Value.Value);
        Assert.Null(_requests.TakeQueued(Standby));

        _time.Advance(TimeSpan.FromMilliseconds(150));
        Assert.NotNull(Write(121_525)); // a slow turn goes out at once again
    }

    [Fact]
    public void Values_x_plane_has_not_answered_are_shown_until_shortly_after_it_answers()
    {
        int request = Write(121_505)!.Value;
        _time.Advance(TimeSpan.FromSeconds(30)); // an X-Plane that is far behind

        Assert.Equal(121_505, _requests.Apply(XPlane).Com1.StandbyKhz);

        _requests.Answered(request, success: true);
        _time.Advance(TimeSpan.FromMilliseconds(400));
        Assert.Equal(121_505, _requests.Apply(XPlane).Com1.StandbyKhz); // the value is on its way back

        _time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(121_500, _requests.Apply(XPlane).Com1.StandbyKhz); // X-Plane's own value decides again
    }

    [Fact]
    public void A_held_back_value_is_shown_too_and_waits_for_its_own_answer()
    {
        int first = Write(121_505)!.Value;
        _time.Advance(TimeSpan.FromMilliseconds(20));
        _requests.PlanWrite(Standby, 121_510, SetStandby(121_510));
        Assert.Equal(121_510, _requests.Apply(XPlane).Com1.StandbyKhz);

        var queued = _requests.TakeQueued(Standby)!.Value;
        _requests.Sent(_nextRequest++, Standby, expectation: queued.Expectation);
        _requests.Answered(first, success: true);
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(121_510, _requests.Apply(XPlane).Com1.StandbyKhz); // the newest is still unanswered
    }

    [Fact]
    public void A_refused_write_stops_being_shown_at_once()
    {
        int request = Write(121_505)!.Value;

        _requests.Answered(request, success: false);

        Assert.Equal(121_500, _requests.Apply(XPlane).Com1.StandbyKhz);
    }

    [Fact]
    public void Answers_in_time_are_not_slow()
    {
        int request = Write(121_505)!.Value;
        _time.Advance(TimeSpan.FromMilliseconds(50));
        _requests.Answered(request, success: true);

        Assert.Null(_requests.SlowSeconds());
    }

    [Fact]
    public void An_unanswered_request_makes_x_plane_slow_after_two_seconds()
    {
        Write(121_505);
        _time.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Null(_requests.SlowSeconds());

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2.5, _requests.SlowSeconds()!.Value, 3);
    }

    [Fact]
    public void A_slow_answer_is_remembered_for_a_while_and_a_fast_one_ends_it()
    {
        int slow = Write(121_505)!.Value;
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(30), _requests.Answered(slow, success: true));

        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(30, _requests.SlowSeconds()!.Value, 3); // still the last word on how X-Plane answers

        int fast = Write(121_510)!.Value;
        _time.Advance(TimeSpan.FromMilliseconds(40));
        _requests.Answered(fast, success: true);
        Assert.Null(_requests.SlowSeconds());
    }

    [Fact]
    public void While_requests_are_out_the_last_known_delay_stands_however_long_ago_it_was_measured()
    {
        int first = Write(121_505)!.Value;
        _time.Advance(TimeSpan.FromSeconds(30));
        _requests.Answered(first, success: true); // X-Plane is 30 s behind
        _time.Advance(TimeSpan.FromSeconds(15));
        Write(121_510);                            // a new change, 15 s later
        _time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(30, _requests.SlowSeconds()!.Value, 3); // not "5 s": X-Plane is still 30 s behind as far as anyone knows
    }

    [Fact]
    public void A_request_never_answered_is_forgotten_eventually()
    {
        Write(121_505);
        _time.Advance(XPlaneRequests.MaxWait + TimeSpan.FromSeconds(1));

        Assert.Equal(121_500, _requests.Apply(XPlane).Com1.StandbyKhz);
        Assert.Null(_requests.SlowSeconds());
    }

    [Fact]
    public void An_answer_to_a_request_sent_elsewhere_is_ignored() =>
        Assert.Null(_requests.Answered(999, success: true));
}
