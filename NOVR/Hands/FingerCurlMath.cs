using System;
using UnityEngine;

namespace NOVR.MotionControls;

// How far each finger is closed: 0 is a flat hand, 1 is the glove's fist.
// The thumb is not here. Controllers sense it by what it touches, which the
// glove already shows.
internal struct FingerCurls
{
    public float Index, Middle, Ring, Little;
}

// Turns OpenXR hand joints (XR_EXT_hand_tracking order) into finger curls.
// Only joint positions are used: the bend between one bone and the next is the
// same in any coordinate system, so this does not depend on how a runtime
// orients its joints or on the conversion from OpenXR to Unity axes.
internal static class FingerCurlMath
{
    internal const int JointCount = 26;
    // First joint (metacarpal) of each finger; proximal, intermediate, distal
    // and tip follow it.
    internal const int IndexMetacarpal = 6, MiddleMetacarpal = 11, RingMetacarpal = 16, LittleMetacarpal = 21;
    // Total bend, in degrees, of each finger in the bundled glove's fist pose.
    // The same bend on the glove then matches the bend the runtime reports.
    internal const float IndexFist = 286f, MiddleFist = 286f, RingFist = 274f, LittleFist = 262f;

    internal static bool TryCurls(Vector3[]? joints, out FingerCurls curls)
    {
        curls = default;
        if (joints == null || joints.Length < JointCount)
            return false;
        return TryCurl(joints, IndexMetacarpal, IndexFist, out curls.Index) &&
            TryCurl(joints, MiddleMetacarpal, MiddleFist, out curls.Middle) &&
            TryCurl(joints, RingMetacarpal, RingFist, out curls.Ring) &&
            TryCurl(joints, LittleMetacarpal, LittleFist, out curls.Little);
    }

    private static bool TryCurl(Vector3[] joints, int metacarpal, float fist, out float curl)
    {
        curl = 0;
        var bend = 0f;
        for (var i = 0; i < 3; i++)
        {
            if (!TryBend(joints[metacarpal + i], joints[metacarpal + i + 1], joints[metacarpal + i + 2], out var angle))
                return false;
            bend += angle;
        }
        curl = FlightControlMath.Clamp(bend / fist, 0f, 1f);
        return true;
    }

    // Angle in degrees between the bone a-b and the bone b-c.
    private static bool TryBend(Vector3 a, Vector3 b, Vector3 c, out float degrees)
    {
        degrees = 0;
        double ux = b.x - a.x, uy = b.y - a.y, uz = b.z - a.z;
        double vx = c.x - b.x, vy = c.y - b.y, vz = c.z - b.z;
        var lengths = Math.Sqrt(ux * ux + uy * uy + uz * uz) * Math.Sqrt(vx * vx + vy * vy + vz * vz);
        // Joints stacked on one point, or a non-finite position, carry no bend.
        if (!(lengths > 1e-8) || double.IsInfinity(lengths))
            return false;
        var cosine = (ux * vx + uy * vy + uz * vz) / lengths;
        degrees = (float)(Math.Acos(Math.Max(-1, Math.Min(1, cosine))) * 180 / Math.PI);
        return FlightControlMath.IsFinite(degrees);
    }
}
