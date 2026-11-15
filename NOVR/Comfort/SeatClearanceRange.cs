using System;

namespace NOVR.Comfort;

// Keep config validation, the menu slider and camera-time sanitation aligned.
internal static class SeatClearanceRange
{
    internal const float Minimum = .05f, Maximum = .40f, Default = .25f;

    // Cutting a seat rebuilds and uploads its whole mesh, and a seated head is
    // never still to the half millimetre. Keep one cut while the head stays
    // within this distance of where the cut was made. The dead band shrinks
    // with the radius so a small clearance keeps most of its size.
    internal const float MaximumDeadband = .01f;
    private const float DeadbandShare = .04f;

    internal static float Deadband(float radius) =>
        float.IsNaN(radius) || radius <= 0 ? 0 : Math.Min(MaximumDeadband, radius * DeadbandShare);

    // The cached centre may lag the head by one dead band. Cutting this much
    // less keeps every removed point inside the radius the user asked for.
    internal static float CutRadius(float radius) => radius - Deadband(radius);

    internal static bool WithinDeadband(float distanceSquared, float radius)
    {
        var deadband = Deadband(radius);
        return distanceSquared < deadband * deadband;
    }

    internal static bool TryClamp(float value, out float radius)
    {
        radius = 0;
        if (float.IsNaN(value) || float.IsInfinity(value))
            return false;
        radius = Math.Max(Minimum, Math.Min(Maximum, value));
        return true;
    }
}
