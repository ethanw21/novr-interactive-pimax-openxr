using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;

namespace NOVR;

[HarmonyPatch(typeof(Camera), "set_fieldOfView")]
public static class CameraPatches
{
    // The game sets the main camera's FOV every frame. Remember that camera's
    // URP data so the usual call does not search its components again.
    private static Camera? _lastCamera;
    private static UniversalAdditionalCameraData? _lastData;

    [HarmonyPrefix]
    private static bool AllowNonHeadsetFov(Camera __instance)
    {
        // The runtime owns headset projection. Suppress game zoom on those
        // cameras, including repeated setters that otherwise flood Unity's log.
        // Instrument, preview and desktop cameras must retain their native FOV:
        // blocking every setter breaks target tracking and landing-camera zoom.
        if (!XRSettings.enabled || __instance.targetTexture != null ||
            __instance.cameraType is not (CameraType.Game or CameraType.VR))
            return true;

        // URP chooses XR through additional camera data. stereoTargetEye alone
        // is not sufficient to opt an URP camera out of headset rendering.
        if (!ReferenceEquals(__instance, _lastCamera) || _lastData == null)
        {
            _lastCamera = __instance;
            _lastData = __instance.GetComponent<UniversalAdditionalCameraData>();
        }
        var data = _lastData;
        // Without additional data, URP defaults to XR rendering as well.
        return data != null && !data.allowXRRendering;
    }
}
