#if CPP
using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
#endif
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace NOVR.VrCamera;

public class VrCameraManager : MonoBehaviour
{
    private const string NuclearOptionMainCameraName = "Main Camera";
    private const string NuclearOptionMenuCameraName = "Menu Camera";
    private const string VrCameraChildName = "NOVR Main Camera";
    private static readonly string[] TrackedChildNames = { "cockpitRenderer", "postProcessingRenderer" };

    public static HashSet<Camera> IgnoredCameras = new();
    private static readonly System.Predicate<Camera> IsDestroyedCamera = camera => camera == null;
    private Camera[] _cameraBuffer = System.Array.Empty<Camera>();
    private int _cameraCount;
    // Cameras already seen with another name. Reading a name allocates a
    // string, so each of these is asked once instead of on every frame.
    private readonly HashSet<Camera> _unrelatedCameras = new();

    private void Update() // Todo: Make me behave on events if possible
    {
        // Scene cameras leave managed wrappers behind after Unity destroys them.
        // Retain live owners (including disabled cameras), not every old scene.
        IgnoredCameras.RemoveWhere(IsDestroyedCamera);
        _unrelatedCameras.RemoveWhere(IsDestroyedCamera);
        // Keep discovering scene/menu cameras every frame, but reuse storage
        // instead of allocating an array for every headset frame.
        var capacity = Camera.allCamerasCount;
        if (_cameraBuffer.Length < capacity)
            _cameraBuffer = new Camera[Mathf.NextPowerOfTwo(capacity)];
        var count = Camera.GetAllCameras(_cameraBuffer);
        if (count < _cameraCount)
            System.Array.Clear(_cameraBuffer, count, _cameraCount - count);
        _cameraCount = count;

        for (var i = 0; i < count; i++)
        {
            var camera = _cameraBuffer[i];
            if (camera == null || _unrelatedCameras.Contains(camera))
                continue;
            var gameObject = camera.gameObject;
            var name = gameObject.name;
            if (name is not (NuclearOptionMainCameraName or NuclearOptionMenuCameraName))
            {
                _unrelatedCameras.Add(camera);
                continue;
            }

            var isMainCamera = name == NuclearOptionMainCameraName;
            if (isMainCamera)
            {
                var existingTrackedCamera = GetTrackedMainCamera(gameObject);
                if (existingTrackedCamera != null)
                {
                    EnsureTrackedMainCameraRig(camera, existingTrackedCamera);
                    continue;
                }
            }

            if (IgnoredCameras.Contains(camera))
            {
                continue;
            }

            if (isMainCamera)
            {
                SetUpMainCameraRig(camera);
            }
            else
            {
                HandleChildCameras(camera);
                gameObject.AddComponent<VrCamera>();
                IgnoredCameras.Add(camera);
            }
        }
    }

    public static Camera? GetTrackedMainCamera(GameObject gameCameraRoot)
    {
        var trackedCameraTransform = gameCameraRoot.transform.Find(VrCameraChildName);
        return trackedCameraTransform != null ? trackedCameraTransform.GetComponent<Camera>() : null;
    }

    private void SetUpMainCameraRig(Camera rootCamera)
    {
        HandleChildCameras(rootCamera);

        var existingTrackedCamera = GetTrackedMainCamera(rootCamera.gameObject);
        if (existingTrackedCamera != null)
        {
            EnsureTrackedMainCameraRig(rootCamera, existingTrackedCamera);
            return;
        }

        var trackedCameraObject = new GameObject(VrCameraChildName);
        trackedCameraObject.transform.SetParent(rootCamera.transform, false);
        trackedCameraObject.tag = rootCamera.tag;

        var trackedCamera = trackedCameraObject.AddComponent<Camera>();
        trackedCamera.CopyFrom(rootCamera);
        var universalAdditionalCameraData = trackedCameraObject.AddComponent<UniversalAdditionalCameraData>();
        universalAdditionalCameraData.renderType = CameraRenderType.Base;
        universalAdditionalCameraData.allowXRRendering = true;
        var rootUniversalAdditionalCameraData = rootCamera.GetComponent<UniversalAdditionalCameraData>();

        if (universalAdditionalCameraData != null && rootUniversalAdditionalCameraData != null)
            universalAdditionalCameraData.cameraStack.AddRange(rootUniversalAdditionalCameraData.cameraStack);


        var rootAudioListener = rootCamera.GetComponent<AudioListener>();
        if (rootAudioListener != null)
        {
            var trackedAudioListener = trackedCameraObject.AddComponent<AudioListener>();
            trackedAudioListener.enabled = rootAudioListener.enabled;
            rootAudioListener.enabled = false;
        }

        rootCamera.tag = "Untagged";
        rootCamera.enabled = false;
        ReparentTrackedChildren(rootCamera.transform, trackedCameraObject.transform);

        trackedCameraObject.AddComponent<VrCamera>();
        trackedCameraObject.AddComponent<VrCockpitNearClip>();

        IgnoredCameras.Add(rootCamera);
        IgnoredCameras.Add(trackedCamera);
    }

    private static void EnsureTrackedMainCameraRig(Camera rootCamera, Camera trackedCamera)
    {
        if (rootCamera.enabled)
        {
            rootCamera.enabled = false;
        }

        if (rootCamera.CompareTag("MainCamera"))
        {
            rootCamera.tag = "Untagged";
        }

        if (!trackedCamera.enabled)
        {
            trackedCamera.enabled = true;
        }

        if (!trackedCamera.CompareTag("MainCamera"))
        {
            trackedCamera.tag = "MainCamera";
        }

        if (trackedCamera.GetComponent<VrCamera>() == null)
        {
            trackedCamera.gameObject.AddComponent<VrCamera>();
        }
        if (trackedCamera.GetComponent<VrCockpitNearClip>() == null)
            trackedCamera.gameObject.AddComponent<VrCockpitNearClip>();

        IgnoredCameras.Add(rootCamera);
        IgnoredCameras.Add(trackedCamera);
    }

    private void HandleChildCameras(Camera parentCamera)
    {
        foreach (var child in parentCamera.GetComponentsInChildren<Camera>())
        {
            if (child != parentCamera && !IgnoredCameras.Contains(child) && child.GetComponent<StereoCamera>() == null)
                child.gameObject.AddComponent<StereoCamera>();
        }
    }

    private static void ReparentTrackedChildren(Transform rootCameraTransform, Transform trackedCameraTransform)
    {
        foreach (var childName in TrackedChildNames)
        {
            var child = rootCameraTransform.Find(childName);
            if (child != null)
            {
                child.SetParent(trackedCameraTransform, false);
            }
        }
    }
}
