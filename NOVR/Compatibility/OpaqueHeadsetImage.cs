using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR;

namespace NOVR.Compatibility;

// Applies HeadsetImagePolicy. The layer flags are a setting of the bundled
// OpenXR plugin. The alpha rewrite touches only the runtime's eye textures,
// after every camera and the desktop mirror have finished. Game cameras,
// render textures and the desktop view are never changed.
internal sealed class OpaqueHeadsetImage : MonoBehaviour
{
    private const string OpenXRPlugin = "UnityOpenXR";
    private const int FillAttempts = 3;
    [DllImport(OpenXRPlugin, EntryPoint = "ext_composition_layers_SetDefaultSceneLayerFlags")]
    private static extern void NativeSetSceneLayerFlags(ulong flags);
    [DllImport(OpenXRPlugin, EntryPoint = "ext_composition_layers_ClearDefaultSceneLayerFlags")]
    private static extern void NativeClearSceneLayerFlags();
    // Portable checks replace these; the game always calls the bundled plugin.
    internal static Action<ulong> SetSceneLayerFlags = NativeSetSceneLayerFlags;
    internal static Action ClearSceneLayerFlags = NativeClearSceneLayerFlags;

    internal static string Status { get; private set; } = "Waiting for the VR runtime.";
    private readonly List<XRDisplaySubsystem> _displays = new();
    private XRDisplaySubsystem? _display;
    private HeadsetImageOpacity _mode;
    private bool _applied, _xr, _layerOpaque, _layerFault, _fill, _targetsReported;
    private int _fillFaults;
    private Material? _material;
    private Mesh? _mesh;

    private void OnEnable()
    {
        _applied = _layerFault = _targetsReported = false;
        _fillFaults = 0;
        _display = null;
        RenderPipelineManager.endFrameRendering += AfterFrame;
        // Core starts OpenXR after its components are enabled, so the very
        // first submitted frame already uses the requested layer flags.
        Refresh();
    }

    private void Update() => Refresh();

    private void Refresh()
    {
        var mode = HeadsetImagePolicy.Normalize((int)ModConfiguration.Instance.HeadsetImageOpacity.Value);
        var xr = XRSettings.enabled;
        if (_applied && mode == _mode && xr == _xr)
            return;
        _applied = true;
        _mode = mode;
        _xr = xr;
        ApplyLayer(HeadsetImagePolicy.SubmitsOpaqueLayer(mode), xr);
        if (!xr)
        {
            _fill = false;
            Status = "VR runtime is not active.";
            return;
        }
        var runtime = RuntimeName();
        _fillFaults = 0; // A new session or setting earns a fresh attempt.
        _fill = HeadsetImagePolicy.WritesOpaqueAlpha(mode, runtime);
        Status = $"runtime={runtime}; mode={mode}; opaque layer=" +
            (_layerFault ? "unavailable" : _layerOpaque ? "requested" : "plugin default") +
            "; alpha rewrite=" + (_fill ? "on" : "off");
        Debug.Log("[NOVR] Headset image: " + Status + ".");
    }

    private void ApplyLayer(bool opaque, bool xr)
    {
        try
        {
            if (opaque)
                SetSceneLayerFlags(HeadsetImagePolicy.OpaqueLayerFlags);
            else if (_layerOpaque)
                ClearSceneLayerFlags(); // Only withdraw NOVR's own request.
            _layerOpaque = opaque;
            _layerFault = false;
        }
        catch (Exception error)
        {
            // The plugin may not be loadable before OpenXR starts. Refresh
            // runs again when XR becomes active, so only that failure is final.
            _layerOpaque = false;
            _layerFault = true;
            if (xr)
                Debug.LogWarning("[NOVR] The OpenXR plugin did not accept scene-layer flags: " + error.GetType().Name);
        }
    }

    private static string RuntimeName()
    {
        try
        {
            var name = OpenXRRuntime.name;
            return string.IsNullOrWhiteSpace(name) ? "OpenXR" : name;
        }
        catch (Exception) { return "OpenXR"; }
    }

    private void AfterFrame(ScriptableRenderContext context, Camera[] cameras)
    {
        if (!_fill)
            return;
        var camera = HeadsetCamera(cameras);
        if (camera == null)
            return;
        try
        {
            Fill(context, camera);
            _fillFaults = 0;
        }
        catch (Exception error)
        {
            // Never let a compatibility aid interrupt rendering. A session
            // change can fail one frame; repeated failures are not transient.
            if (++_fillFaults < FillAttempts)
                return;
            _fill = false;
            Status += " (stopped)";
            Debug.LogWarning("[NOVR] Headset image alpha rewrite stopped: " + error.GetType().Name + ": " + error.Message);
        }
    }

    // Reflection probes, instrument cameras and manual renders use the same
    // callback. Only a frame that drew to the headset needs its alpha rewritten.
    private static Camera? HeadsetCamera(Camera[] cameras)
    {
        if (cameras == null)
            return null;
        foreach (var camera in cameras)
            if (camera != null && camera.cameraType == CameraType.Game && camera.targetTexture == null &&
                camera.stereoTargetEye != StereoTargetEyeMask.None)
                return camera;
        return null;
    }

    private void Fill(ScriptableRenderContext context, Camera camera)
    {
        var display = Display();
        if (display == null)
            return;
        var passes = display.GetRenderPassCount();
        if (passes <= 0)
            return;
        var material = FillMaterial();
        var mesh = FillMesh();
        var command = CommandBufferPool.Get("NOVR opaque headset image");
        try
        {
            var target = default(RenderTextureDescriptor);
            var slices = 0;
            command.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
            for (var index = 0; index < passes; index++)
            {
                display.GetRenderPass(index, out var pass);
                var views = pass.GetRenderParameterCount();
                for (var view = 0; view < views; view++)
                {
                    pass.GetRenderParameter(camera, view, out var parameter);
                    // Binding resets the viewport to the whole eye texture.
                    command.SetRenderTarget(pass.renderTarget, 0, CubemapFace.Unknown, parameter.textureArraySlice);
                    command.DrawMesh(mesh, Matrix4x4.identity, material, 0, 0);
                    slices++;
                }
                target = pass.renderTargetDesc;
            }
            context.ExecuteCommandBuffer(command);
            context.Submit();
            if (_targetsReported)
                return;
            _targetsReported = true;
            Debug.Log($"[NOVR] Headset image alpha rewrite: {slices} eye image(s) in {passes} pass(es), " +
                $"{target.width}x{target.height}, {target.graphicsFormat}, MSAA {target.msaaSamples}.");
        }
        finally { CommandBufferPool.Release(command); }
    }

    private XRDisplaySubsystem? Display()
    {
        if (_display != null && _display.running)
            return _display;
        _display = null;
        SubsystemManager.GetSubsystems(_displays);
        foreach (var display in _displays)
            if (display != null && display.running)
            {
                _display = display;
                break;
            }
        _displays.Clear();
        return _display;
    }

    // Always shipped with the player. Colour (0,0,0,1) blended One/OneMinusSrcColor
    // leaves RGB untouched and sets alpha to exactly one.
    private Material FillMaterial()
    {
        if (_material != null)
            return _material;
        var shader = Shader.Find("Hidden/Internal-Colored");
        if (shader == null)
            throw new InvalidOperationException("Built-in colour shader is missing.");
        _material = new Material(shader) { name = "NOVR opaque headset image", hideFlags = HideFlags.HideAndDontSave };
        _material.SetColor("_Color", new Color(0, 0, 0, 1));
        _material.SetFloat("_SrcBlend", (float)BlendMode.One);
        _material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcColor);
        _material.SetFloat("_ZWrite", 0);
        _material.SetFloat("_ZTest", (float)CompareFunction.Always);
        _material.SetFloat("_Cull", (float)CullMode.Off);
        return _material;
    }

    // One clip-space triangle covering the target. The shader multiplies by vertex colour.
    private Mesh FillMesh()
    {
        if (_mesh != null)
            return _mesh;
        _mesh = new Mesh { name = "NOVR opaque headset image", hideFlags = HideFlags.HideAndDontSave };
        _mesh.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(-1, 3, 0), new Vector3(3, -1, 0) };
        _mesh.colors = new[] { Color.white, Color.white, Color.white };
        _mesh.triangles = new[] { 0, 1, 2 };
        return _mesh;
    }

    private void OnDisable()
    {
        RenderPipelineManager.endFrameRendering -= AfterFrame;
        _fill = false;
    }

    private void OnDestroy()
    {
        if (_material != null)
            Destroy(_material);
        if (_mesh != null)
            Destroy(_mesh);
    }
}
