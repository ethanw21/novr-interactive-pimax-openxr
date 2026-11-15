using HarmonyLib;
using UnityEngine;

namespace NOVR.VrCamera;

public class CameraCockpitStatePatch
{
    [HarmonyPatch(typeof(CameraCockpitState), nameof(CameraCockpitState.EnterState))]
    private static class EnterStatePatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            // Native chase/orbit exits restore a hard-coded 2 km shadow range.
            // Honour the user's saved range/cascades when returning to VR flight.
            if (UnityEngine.XR.XRSettings.enabled && PlayerSettings.graphics != null)
                PlayerSettings.graphics.SetShadowDistance(PlayerSettings.graphics.ShadowDistance);
        }
    }

    [HarmonyPatch(typeof(CameraCockpitState), "UpdateState")]
    private static class UpdateStatePatch
    {
        // Runs every frame in the cockpit. Injected references write the
        // fields directly, where reflection boxed two floats per frame.
        [HarmonyPostfix]
        private static void Postfix(ref float ___panView, ref float ___tiltView)
        {
            ___panView = 0.0f;
            ___tiltView = 0.0f;
        }
    }
}
