using UnityEngine;
using UnityEngine.XR;

namespace NOVR.Performance;

// Applies FramePacingPolicy to the two engine settings it covers.
internal sealed class FramePacingGuard : MonoBehaviour
{
    internal static string Status { get; private set; } = FramePacingPolicy.Describe(default, false);

    private FramePacingState _state;
    private bool _enabled;

    private void Update() => Apply(ModConfiguration.Current?.Performance.HoldOffFrameLimit.Value == true);

    private void OnDisable() => Apply(false);

    private void Apply(bool enabled)
    {
        var before = _state;
        var write = FramePacingPolicy.Decide(ref _state, enabled, XRSettings.enabled && XRSettings.isDeviceActive,
            Application.targetFrameRate, QualitySettings.vSyncCount);
        if (write.SetTargetFrameRate)
            Application.targetFrameRate = write.TargetFrameRate;
        if (write.SetVSyncCount)
            QualitySettings.vSyncCount = write.VSyncCount;
        if (before.Holding == _state.Holding && before.GameTargetFrameRate == _state.GameTargetFrameRate &&
            before.GameVSyncCount == _state.GameVSyncCount && enabled == _enabled)
            return;
        _enabled = enabled;
        Status = FramePacingPolicy.Describe(_state, enabled);
        if (write.Stopped)
            Debug.Log($"[NOVR] Frame pacing: handed back the game's frame limit (targetFrameRate={_state.GameTargetFrameRate}, vSyncCount={_state.GameVSyncCount}).");
        else if (_state.Holding)
            Debug.Log($"[NOVR] Frame pacing: the game asked for targetFrameRate={_state.GameTargetFrameRate}, vSyncCount={_state.GameVSyncCount}. " +
                (FramePacingPolicy.Limits(_state.GameTargetFrameRate, _state.GameVSyncCount)
                    ? "Held off while the headset is active."
                    : "Nothing to hold off."));
    }
}
