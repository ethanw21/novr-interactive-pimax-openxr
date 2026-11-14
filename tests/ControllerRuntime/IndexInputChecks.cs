using NOVR.MotionControls;
using UnityEngine;

// Swipe recognition and finger curl, exercised without the controller reader.
internal static class IndexInputChecks
{
    // A hand whose four fingers are each closed by the given fraction of the
    // glove's fist. Each finger bends evenly at its three joints, in one plane.
    internal static Vector3[] Hand(float index, float middle, float ring, float little)
    {
        var joints = new Vector3[FingerCurlMath.JointCount];
        Finger(joints, FingerCurlMath.IndexMetacarpal, .03f, index * FingerCurlMath.IndexFist);
        Finger(joints, FingerCurlMath.MiddleMetacarpal, .01f, middle * FingerCurlMath.MiddleFist);
        Finger(joints, FingerCurlMath.RingMetacarpal, -.01f, ring * FingerCurlMath.RingFist);
        Finger(joints, FingerCurlMath.LittleMetacarpal, -.03f, little * FingerCurlMath.LittleFist);
        return joints;
    }

    private static void Finger(Vector3[] joints, int metacarpal, float side, float degrees)
    {
        double angle = 0, y = 1, z = -.2;
        joints[metacarpal] = new Vector3(side, (float)y, (float)z);
        for (var i = 1; i <= 4; i++)
        {
            z -= .03 * Math.Cos(angle);
            y -= .03 * Math.Sin(angle);
            joints[metacarpal + i] = new Vector3(side, (float)y, (float)z);
            angle += degrees / 3 * Math.PI / 180;
        }
    }

    internal static void RunSwipes(Action<bool> yes)
    {
        const float frame = 1f / 90;
        TrackpadSwipeDirection Stroke(TrackpadSwipe swipe, ref float time, float fromX, float fromY, float toX, float toY, int frames,
            bool pressed = false, float distance = TrackpadSwipe.DefaultDistance)
        {
            var seen = TrackpadSwipeDirection.None;
            for (var i = 1; i <= frames; i++)
            {
                time += frame;
                var result = swipe.Update(true, fromX + (toX - fromX) * i / frames, fromY + (toY - fromY) * i / frames, pressed, time, distance);
                if (result != TrackpadSwipeDirection.None)
                    seen = result;
            }
            return seen;
        }
        TrackpadSwipe Touching(ref float time, float x, float y)
        {
            var swipe = new TrackpadSwipe();
            swipe.Update(true, x, y, false, time, TrackpadSwipe.DefaultDistance);
            time += TrackpadSwipe.Settle + frame;
            swipe.Update(true, x, y, false, time, TrackpadSwipe.DefaultDistance);
            return swipe;
        }

        var now = 10f;
        var up = Touching(ref now, 0, -.5f);
        yes(Stroke(up, ref now, 0, -.5f, 0, .5f, 10) == TrackpadSwipeDirection.Up);
        var down = Touching(ref now, 0, .5f);
        yes(Stroke(down, ref now, 0, .5f, 0, -.5f, 10) == TrackpadSwipeDirection.Down);

        // A thumb that drifts the same distance slowly is repositioning, not swiping.
        var slow = Touching(ref now, 0, -.5f);
        yes(Stroke(slow, ref now, 0, -.5f, 0, .5f, 90) == TrackpadSwipeDirection.None);
        // After a pause on the pad, a quick stroke from there still counts.
        yes(Stroke(slow, ref now, 0, .5f, 0, -.5f, 10) == TrackpadSwipeDirection.Down);

        // A short flick, a sideways stroke and a diagonal one are not swipes.
        var shortFlick = Touching(ref now, 0, 0);
        yes(Stroke(shortFlick, ref now, 0, 0, 0, .6f, 8) == TrackpadSwipeDirection.None);
        var sideways = Touching(ref now, -.8f, 0);
        yes(Stroke(sideways, ref now, -.8f, 0, .8f, .1f, 10) == TrackpadSwipeDirection.None);
        var diagonal = Touching(ref now, -.5f, -.5f);
        yes(Stroke(diagonal, ref now, -.5f, -.5f, .3f, .5f, 10) == TrackpadSwipeDirection.None);

        // A small move the other way first does not shorten the swipe.
        var windup = Touching(ref now, 0, -.3f);
        yes(Stroke(windup, ref now, 0, -.3f, 0, -.5f, 3) == TrackpadSwipeDirection.None);
        yes(Stroke(windup, ref now, 0, -.5f, 0, .3f, 8) == TrackpadSwipeDirection.Up);

        // Pressing the pad moves the contact patch; that is never a swipe, and
        // travel made under a press does not count once it is released.
        var pressed = Touching(ref now, 0, -.5f);
        yes(Stroke(pressed, ref now, 0, -.5f, 0, .5f, 10, pressed: true) == TrackpadSwipeDirection.None);
        yes(Stroke(pressed, ref now, 0, .5f, 0, .6f, 3) == TrackpadSwipeDirection.None);
        yes(Stroke(pressed, ref now, 0, .6f, 0, -.4f, 10) == TrackpadSwipeDirection.Down);

        // A position that jumps, such as a pad snapping back to its centre as
        // the thumb lifts, is a sensor artefact.
        var jump = Touching(ref now, 0, .9f);
        yes(Stroke(jump, ref now, 0, .9f, 0, .85f, 3) == TrackpadSwipeDirection.None);
        now += frame;
        yes(jump.Update(true, 0, 0, false, now, TrackpadSwipe.DefaultDistance) == TrackpadSwipeDirection.None && jump.Scroll == 0);
        now += frame;
        yes(jump.Update(false, 0, 0, false, now, TrackpadSwipe.DefaultDistance) == TrackpadSwipeDirection.None);
        // Even at the shortest configured distance one step is never a swipe.
        var coarse = Touching(ref now, 0, 0);
        yes(Stroke(coarse, ref now, 0, 0, 0, .29f, 1, distance: TrackpadSwipe.MinDistance) == TrackpadSwipeDirection.None);
        yes(Stroke(coarse, ref now, 0, .29f, 0, .65f, 3, distance: TrackpadSwipe.MinDistance) == TrackpadSwipeDirection.Up);

        // The first reports of a touch may still hold the last position.
        var settling = new TrackpadSwipe();
        now += 1;
        yes(settling.Update(true, 0, -.9f, false, now, TrackpadSwipe.DefaultDistance) == TrackpadSwipeDirection.None);
        now += frame;
        yes(settling.Update(true, 0, .9f, false, now, TrackpadSwipe.DefaultDistance) == TrackpadSwipeDirection.None);

        // The tap lasts a fixed time, reads the same twice in one frame, outlives
        // the thumb lifting, and ends on reset.
        var tap = Touching(ref now, 0, -.5f);
        yes(Stroke(tap, ref now, 0, -.5f, 0, .5f, 10) == TrackpadSwipeDirection.Up);
        yes(tap.Update(true, 0, .5f, false, now, TrackpadSwipe.DefaultDistance) == TrackpadSwipeDirection.Up);
        now += TrackpadSwipe.Pulse * .5f;
        yes(tap.Update(false, 0, 0, false, now, TrackpadSwipe.DefaultDistance) == TrackpadSwipeDirection.Up);
        now += TrackpadSwipe.Pulse;
        yes(tap.Update(false, 0, 0, false, now, TrackpadSwipe.DefaultDistance) == TrackpadSwipeDirection.None);
        var reset = Touching(ref now, 0, -.5f);
        yes(Stroke(reset, ref now, 0, -.5f, 0, .5f, 10) == TrackpadSwipeDirection.Up);
        reset.Reset();
        now += frame;
        yes(reset.Update(true, 0, .5f, false, now, TrackpadSwipe.DefaultDistance) == TrackpadSwipeDirection.None);
        // Non-finite input cancels instead of producing a direction.
        var invalid = Touching(ref now, 0, -.5f);
        now += frame;
        yes(invalid.Update(true, 0, float.NaN, false, now, TrackpadSwipe.DefaultDistance) == TrackpadSwipeDirection.None);
        yes(invalid.Update(true, 0, 0, false, float.NaN, TrackpadSwipe.DefaultDistance) == TrackpadSwipeDirection.None);

        // Curl is bend over the glove's fist, whatever the hand's position or
        // the coordinate handedness of the joints.
        yes(FingerCurlMath.TryCurls(Hand(0, .1f, .6f, 1.2f), out var curls));
        yes(Math.Abs(curls.Index) < .001f && Math.Abs(curls.Middle - .1f) < .001f && Math.Abs(curls.Ring - .6f) < .001f && curls.Little == 1);
        var mirrored = Hand(.3f, .3f, .3f, .3f);
        for (var i = 0; i < mirrored.Length; i++)
            mirrored[i] = new Vector3(-mirrored[i].z + 4, mirrored[i].x - 2, -mirrored[i].y);
        yes(FingerCurlMath.TryCurls(mirrored, out curls) && Math.Abs(curls.Index - .3f) < .001f && Math.Abs(curls.Little - .3f) < .001f);
        yes(!FingerCurlMath.TryCurls(null, out _) && !FingerCurlMath.TryCurls(new Vector3[5], out _));
        var broken = Hand(.5f, .5f, .5f, .5f);
        broken[FingerCurlMath.RingMetacarpal + 2] = new Vector3(float.NaN, 0, 0);
        yes(!FingerCurlMath.TryCurls(broken, out _));
    }
}
