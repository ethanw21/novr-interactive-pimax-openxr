namespace NOVR.Performance;

internal struct FramePacingState
{
    // True while the game's own limits are being held off.
    internal bool Holding;
    // What the game last asked for, to hand back when the headset stops.
    internal int GameTargetFrameRate, GameVSyncCount;
}

internal readonly struct FramePacingWrite
{
    internal readonly bool SetTargetFrameRate, SetVSyncCount, Started, Stopped;
    internal readonly int TargetFrameRate, VSyncCount;

    internal FramePacingWrite(bool setTarget, int target, bool setVSync, int vSync, bool started, bool stopped)
    {
        SetTargetFrameRate = setTarget;
        TargetFrameRate = target;
        SetVSyncCount = setVSync;
        VSyncCount = vSync;
        Started = started;
        Stopped = stopped;
    }
}

// A headset paces frames itself. A desktop frame limit or vertical sync on
// top of that can only hold frames back, so both are held off while the
// headset is presenting and handed back unchanged afterwards. The game applies
// its limits when it changes state (60 in menus, the player's own limit in a
// mission), so a new request from the game is remembered, not fought.
internal static class FramePacingPolicy
{
    internal const int Unlimited = -1, NoVSync = 0;

    internal static bool Limits(int targetFrameRate, int vSyncCount) => targetFrameRate > 0 || vSyncCount > 0;

    internal static FramePacingWrite Decide(ref FramePacingState state, bool enabled, bool headsetActive,
        int targetFrameRate, int vSyncCount)
    {
        if (enabled && headsetActive)
        {
            var started = !state.Holding;
            if (started)
            {
                state.Holding = true;
                state.GameTargetFrameRate = targetFrameRate;
                state.GameVSyncCount = vSyncCount;
            }
            else
            {
                if (targetFrameRate != Unlimited)
                    state.GameTargetFrameRate = targetFrameRate;
                if (vSyncCount != NoVSync)
                    state.GameVSyncCount = vSyncCount;
            }
            return new FramePacingWrite(targetFrameRate != Unlimited, Unlimited, vSyncCount != NoVSync, NoVSync, started, false);
        }
        if (!state.Holding)
            return default;
        state.Holding = false;
        // Hand back only what is still held off; anything else was set by the
        // game or the player in the meantime and is theirs.
        return new FramePacingWrite(
            targetFrameRate == Unlimited && state.GameTargetFrameRate != Unlimited, state.GameTargetFrameRate,
            vSyncCount == NoVSync && state.GameVSyncCount != NoVSync, state.GameVSyncCount, false, true);
    }

    internal static string Describe(in FramePacingState state, bool enabled)
    {
        if (!state.Holding)
            return enabled ? "Frame limit: left to the game while no headset is active" : "Frame limit: left to the game";
        if (!Limits(state.GameTargetFrameRate, state.GameVSyncCount))
            return "Frame limit: the game asked for none";
        var asked = state.GameTargetFrameRate > 0 ? state.GameTargetFrameRate + " FPS" : "";
        if (state.GameVSyncCount > 0)
            asked += (asked.Length > 0 ? " and " : "") + "vertical sync";
        return "Frame limit: game asked for " + asked + "; held off in VR";
    }
}
