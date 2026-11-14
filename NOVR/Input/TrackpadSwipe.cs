using System;

namespace NOVR.MotionControls;

internal enum TrackpadSwipeDirection
{
    None, Up, Down
}

// One thumb's swipe state on a touch-sensing trackpad. Engine-independent:
// positions are the runtime's -1..1 pad coordinates, +Y away from the palm.
//
// A swipe is thumb travel of at least the configured distance within a short
// window, mostly along the pad's long axis. A slow drag, a resting thumb, a
// force press and a position jump are not swipes. One touch gives one swipe, so
// the return stroke of a thumb that stays on the pad does nothing.
internal sealed class TrackpadSwipe
{
    internal const float DefaultDistance = .7f, MinDistance = .3f, MaxDistance = 1.5f;
    // Seconds. A swipe reads as a held button for Pulse so that press-and-release
    // actions see both edges and held actions get one short tap.
    internal const float Window = .3f, Settle = .04f, Pulse = .15f;
    // A larger move between two samples is a sensor jump, not thumb travel. The
    // limit also stays below the swipe distance, so one jump is never a swipe.
    internal const float MaxStep = .6f;
    private const int Capacity = 48;
    private readonly float[] _x = new float[Capacity], _y = new float[Capacity], _time = new float[Capacity];
    private int _start, _count;
    private bool _touching, _spent;
    private float _touchTime, _pulseUntil = float.NegativeInfinity, _lastTime = float.NaN;
    private TrackpadSwipeDirection _pulse;

    // Thumb travel along the pad since the previous sample, for menu scrolling.
    internal float Scroll
    {
        get; private set;
    }

    internal void Reset()
    {
        _touching = _spent = false;
        _count = 0;
        _pulse = TrackpadSwipeDirection.None;
        _pulseUntil = float.NegativeInfinity;
        _lastTime = float.NaN;
        Scroll = 0;
    }

    internal TrackpadSwipeDirection Update(bool touched, float x, float y, bool pressed, float time, float distance)
    {
        if (!FlightControlMath.IsFinite(time))
        {
            Reset();
            return TrackpadSwipeDirection.None;
        }
        // A second read in the same frame must not count as another sample.
        if (time == _lastTime)
            return Current(time);
        _lastTime = time;
        Scroll = 0;
        if (!touched || !FlightControlMath.IsFinite(x) || !FlightControlMath.IsFinite(y))
        {
            _touching = _spent = false;
            _count = 0;
            return Current(time);
        }
        if (!_touching)
        {
            _touching = true;
            _touchTime = time;
            _count = 0;
        }
        // The first reports of a touch can still hold the previous position.
        // A force press moves the contact patch without the thumb travelling.
        if (time - _touchTime < Settle || pressed)
        {
            _count = 0;
            return Current(time);
        }
        distance = FlightControlMath.IsFinite(distance) ? FlightControlMath.Clamp(distance, MinDistance, MaxDistance) : DefaultDistance;
        var step = Math.Min(MaxStep, distance * .85f);
        if (_count > 0)
        {
            var last = (_start + _count - 1) % Capacity;
            if (Math.Abs(x - _x[last]) > step || Math.Abs(y - _y[last]) > step)
                _count = 0;
            else
                Scroll = y - _y[last];
        }
        while (_count > 0 && (time - _time[_start] > Window || _count == Capacity))
        {
            _start = (_start + 1) % Capacity;
            _count--;
        }
        var slot = (_start + _count) % Capacity;
        _x[slot] = x;
        _y[slot] = y;
        _time[slot] = time;
        _count++;
        if (_spent || _count < 3)
            return Current(time);
        // Measure from the farthest point in the window, so a small move the
        // other way just before a swipe does not shorten it.
        var lowest = slot;
        var highest = slot;
        for (var i = 0; i < _count; i++)
        {
            var index = (_start + i) % Capacity;
            if (_y[index] < _y[lowest])
                lowest = index;
            if (_y[index] > _y[highest])
                highest = index;
        }
        var up = y - _y[lowest];
        var down = _y[highest] - y;
        var anchor = up >= down ? lowest : highest;
        var travel = Math.Max(up, down);
        if (travel >= distance && travel >= 1.5f * Math.Abs(x - _x[anchor]))
        {
            _pulse = up >= down ? TrackpadSwipeDirection.Up : TrackpadSwipeDirection.Down;
            _pulseUntil = time + Pulse;
            _spent = true;
        }
        return Current(time);
    }

    private TrackpadSwipeDirection Current(float time) => time < _pulseUntil ? _pulse : TrackpadSwipeDirection.None;
}
