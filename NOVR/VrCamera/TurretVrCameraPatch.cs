using System;
using HarmonyLib;
using UnityEngine;

namespace NOVR.VrCamera;

// Aims a manually controlled turret along the headset view. The prefix runs for
// every active turret on every physics step, so it reads the turret's private
// state through Harmony's injected fields and leaves before doing anything else
// for turrets the player is not aiming.
internal static class TurretVrCameraPatch
{
    private static readonly Action<global::Turret, Vector3>? AimTurret = CreateAimTurret();

    private static Action<global::Turret, Vector3>? CreateAimTurret()
    {
        try
        {
            var method = AccessTools.Method(typeof(global::Turret), "AimTurret", new[] { typeof(Vector3) });
            return method == null ? null : AccessTools.MethodDelegate<Action<global::Turret, Vector3>>(method);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("NOVR: Turret.AimTurret(Vector3) is unavailable; manual turrets keep the native aim. " + exception.Message);
            return null;
        }
    }

    [HarmonyPatch(typeof(global::Turret), "FixedUpdate")]
    private static class FixedUpdatePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(global::Turret __instance, bool ___manual, global::Unit ___target,
            global::Aircraft ___aircraft, global::Unit ___attachedUnit, global::WeaponStation ___currentWeaponStation,
            ref float ___lastVectorSent, ref Vector3 ___manualVector)
        {
            if (!___manual || AimTurret == null || ___target != null)
            {
                return true;
            }

            if (___aircraft == null ||
                ___attachedUnit == null ||
                !___aircraft.LocalSim ||
                global::SceneSingleton<global::CameraStateManager>.i.currentState != global::SceneSingleton<global::CameraStateManager>.i.cockpitState)
            {
                return true;
            }

            var vrCamera = APIBus.MainCamera;
            if (vrCamera == null)
            {
                return true;
            }

            // SetVector updates manualVector, which is why it is read by reference.
            __instance.SetVector(vrCamera.transform.forward);

            if (Time.timeSinceLevelLoad - ___lastVectorSent > 0.2f)
            {
                ___aircraft.SetTurretVector(___currentWeaponStation.Number, ___manualVector);
                ___lastVectorSent = Time.timeSinceLevelLoad;
            }

            AimTurret(__instance, ___manualVector);
            return false;
        }
    }
}
