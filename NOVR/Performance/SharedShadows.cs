using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.Universal.Internal;
using UnityEngine.XR;

namespace NOVR.Performance;

// Experimental and off by default: lets the second eye use the sun shadow map
// the first eye drew in the same frame. The render pipeline is patched only
// while the setting is on, and released again when it is turned off.
internal sealed class SharedShadows : MonoBehaviour
{
    private const string HarmonyId = "silentheron.novr.shared-shadows";

    private static Harmony? _harmony;
    private static Action<ScriptableRenderPass, bool>? _setNativeRenderPass;
    private static bool _enabled, _faulted, _warnedMismatch;
    private static string _fault = "";

    // The last real draw into a shadow map, and what the pass computed for it.
    private static ShadowDraw _drawn;
    private static MainLightShadowCasterPass? _pass;
    private static ScriptableRenderer? _renderer;
    private static RTHandle? _texture;
    private static readonly ShadowSliceData[] Slices = new ShadowSliceData[4];
    private static readonly Vector4[] Splits = new Vector4[4];
    private static float _border, _maxDistanceSq;
    private static int _targetWidth, _targetHeight;

    // The camera render in progress.
    private static bool _reusing, _skipDraws, _skippedCulling;
    private static int _reusedCount, _drawnCount, _reportedReused, _reportedDrawn;

    internal static int Reused => _reusedCount;
    internal static int Drawn => _drawnCount;

    // Reads and resets the counts since the last call, for the readout.
    internal static string Describe()
    {
        var reused = _reusedCount - _reportedReused;
        var drawn = _drawnCount - _reportedDrawn;
        _reportedReused = _reusedCount;
        _reportedDrawn = _drawnCount;
        if (_faulted)
            return "Shared shadows: stopped (" + _fault + ")";
        if (!_enabled)
            return "Shared shadows: off";
        if (reused + drawn == 0)
            return "Shared shadows: on, no shadow maps drawn";
        return reused == 0
            ? "Shared shadows: on, but nothing could be shared"
            : "Shared shadows: " + reused + " of " + (reused + drawn) + " shadow maps reused";
    }

    private void Update()
    {
        var wanted = !_faulted && XRSettings.enabled && ModConfiguration.Current?.Performance.ShareShadows.Value == true;
        if (ModConfiguration.Current?.Performance.ShareShadows.Value != true && _faulted)
        {
            // Turning the setting off and on again is a deliberate retry.
            _faulted = false;
            _fault = "";
        }
        if (wanted == _enabled)
            return;
        if (wanted)
            Enable();
        else
            Disable();
    }

    private void OnDisable() => Disable();

    private static void Enable()
    {
        try
        {
            var pass = typeof(MainLightShadowCasterPass);
            _setNativeRenderPass = AccessTools.MethodDelegate<Action<ScriptableRenderPass, bool>>(
                Required(AccessTools.PropertySetter(typeof(ScriptableRenderPass), "useNativeRenderPass")));
            var harmony = new Harmony(HarmonyId);
            _harmony = harmony;
            harmony.Patch(Required(AccessTools.Method(pass, "Setup", new[] { typeof(RenderingData).MakeByRefType() })),
                prefix: Hook(nameof(SetupPrefix)), postfix: Hook(nameof(SetupPostfix)));
            harmony.Patch(Required(AccessTools.Method(pass, "Configure", new[] { typeof(CommandBuffer), typeof(RenderTextureDescriptor) })),
                postfix: Hook(nameof(ConfigurePostfix)));
            harmony.Patch(Required(AccessTools.Method(pass, "Execute", new[] { typeof(ScriptableRenderContext), typeof(RenderingData).MakeByRefType() })),
                prefix: Hook(nameof(ExecutePrefix)), finalizer: Hook(nameof(ExecuteFinalizer)));
            harmony.Patch(Required(AccessTools.Method(typeof(ShadowUtils), "RenderShadowSlice", new[]
                {
                    typeof(CommandBuffer), typeof(ScriptableRenderContext).MakeByRefType(), typeof(ShadowSliceData).MakeByRefType(),
                    typeof(ShadowDrawingSettings).MakeByRefType(), typeof(Matrix4x4), typeof(Matrix4x4)
                })),
                prefix: Hook(nameof(RenderShadowSlicePrefix)));
            harmony.Patch(Required(AccessTools.Method(typeof(UniversalRenderer), "SetupCullingParameters",
                    new[] { typeof(ScriptableCullingParameters).MakeByRefType(), typeof(CameraData).MakeByRefType() })),
                postfix: Hook(nameof(CullingPostfix)));
            Forget();
            _enabled = true;
            Debug.Log("[NOVR] Shared shadows: on. The second eye reuses the first eye's sun shadow map.");
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private static System.Reflection.MethodInfo Required(System.Reflection.MethodInfo? method) =>
        method ?? throw new MissingMethodException("Unsupported render pipeline shadow entry point.");

    private static HarmonyMethod Hook(string name) => new(AccessTools.Method(typeof(SharedShadows), name));

    private static void Disable()
    {
        var wasEnabled = _enabled;
        _enabled = false;
        Forget();
        try
        {
            _harmony?.UnpatchSelf();
        }
        catch (Exception error)
        {
            Debug.LogWarning("[NOVR] Shared shadows: could not release the render pipeline hooks: " + error.GetType().Name);
        }
        _harmony = null;
        if (wasEnabled)
            Debug.Log($"[NOVR] Shared shadows: off. Reused {_reusedCount} shadow maps, drew {_drawnCount}.");
    }

    private static void Forget()
    {
        _drawn = default;
        _pass = null;
        _renderer = null;
        _texture = null;
        _reusing = _skipDraws = _skippedCulling = false;
    }

    // Any surprise turns the feature off for the session; the game's own
    // shadow drawing then runs untouched.
    private static void Fail(Exception error)
    {
        _faulted = true;
        _fault = error.GetType().Name;
        Debug.LogWarning("[NOVR] Shared shadows stopped; the game draws shadows for both eyes again: " + error);
        Disable();
    }

    private static bool TextureAlive(RTHandle? texture) =>
        texture != null && ReferenceEquals(texture, _texture) && texture.rt != null;

    private static void CullingPostfix(UniversalRenderer __instance, ref ScriptableCullingParameters cullingParameters,
        ref CameraData cameraData)
    {
        _skippedCulling = false;
        if (!_enabled)
            return;
        try
        {
            var xr = cameraData.xr;
            var camera = cameraData.camera;
            if (xr == null || camera == null)
                return;
            var reuse = SharedShadowPolicy.CanReuse(_enabled, _drawn, ReferenceEquals(__instance, _renderer),
                TextureAlive(_texture), xr.enabled, xr.singlePassEnabled, xr.multipassId, Time.frameCount, camera.GetInstanceID());
            var asset = UniversalRenderPipeline.asset;
            if (asset == null || !SharedShadowPolicy.CanSkipCasterCulling(reuse, asset.supportsAdditionalLightShadows,
                    cameraData.maxShadowDistance))
                return;
            cullingParameters.cullingOptions &= ~CullingOptions.ShadowCasters;
            _skippedCulling = true;
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private static bool SetupPrefix(MainLightShadowCasterPass __instance, ref RenderingData renderingData, ref bool __result,
        ShadowSliceData[] ___m_CascadeSlices, Vector4[] ___m_CascadeSplitDistances, RTHandle ___m_MainLightShadowmapTexture,
        ref float ___m_CascadeBorder, ref float ___m_MaxShadowDistanceSq, ref int ___m_ShadowCasterCascadesCount,
        ref bool ___m_CreateEmptyShadowmap, ref int ___renderTargetWidth, ref int ___renderTargetHeight)
    {
        _reusing = _skipDraws = false;
        var skippedCulling = _skippedCulling;
        _skippedCulling = false;
        if (!_enabled)
            return true;
        try
        {
            var cameraData = renderingData.cameraData;
            var xr = cameraData.xr;
            var camera = cameraData.camera;
            if (xr == null || camera == null)
                return true;
            var shadows = renderingData.shadowData;
            var light = renderingData.lightData.mainLightIndex;
            var casts = shadows.supportsMainLightShadows && light >= 0 &&
                renderingData.lightData.visibleLights[light].light is { } sun && sun.shadows != LightShadows.None;
            var reuse = ReferenceEquals(__instance, _pass) &&
                SharedShadowPolicy.CanReuse(_enabled, _drawn, ReferenceEquals(cameraData.renderer, _renderer),
                    TextureAlive(___m_MainLightShadowmapTexture), xr.enabled, xr.singlePassEnabled, xr.multipassId,
                    Time.frameCount, camera.GetInstanceID()) &&
                SharedShadowPolicy.SameShadowMap(_drawn, casts, shadows.mainLightShadowCascadesCount,
                    shadows.mainLightShadowmapWidth, shadows.mainLightShadowmapHeight);
            if (!reuse)
            {
                if (skippedCulling && !_warnedMismatch)
                {
                    _warnedMismatch = true;
                    Debug.LogWarning("[NOVR] Shared shadows: the second eye's shadow settings changed after its culling; it has no shadows this frame.");
                }
                return true;
            }
            // Other cameras that draw no shadows still wipe these between the
            // eyes, so hand the pass the first eye's results back.
            Array.Copy(Slices, ___m_CascadeSlices, Slices.Length);
            Array.Copy(Splits, ___m_CascadeSplitDistances, Splits.Length);
            ___m_ShadowCasterCascadesCount = _drawn.Cascades;
            ___m_CascadeBorder = _border;
            ___m_MaxShadowDistanceSq = _maxDistanceSq;
            ___renderTargetWidth = _targetWidth;
            ___renderTargetHeight = _targetHeight;
            ___m_CreateEmptyShadowmap = false;
            _setNativeRenderPass!(__instance, false);
            _reusing = true;
            _reusedCount++;
            __result = true;
            return false;
        }
        catch (Exception error)
        {
            Fail(error);
            return true;
        }
    }

    private static void SetupPostfix(MainLightShadowCasterPass __instance, ref RenderingData renderingData, bool __result,
        ShadowSliceData[] ___m_CascadeSlices, Vector4[] ___m_CascadeSplitDistances, RTHandle ___m_MainLightShadowmapTexture,
        float ___m_CascadeBorder, float ___m_MaxShadowDistanceSq, bool ___m_CreateEmptyShadowmap,
        int ___renderTargetWidth, int ___renderTargetHeight)
    {
        // A pass that draws nothing, or only the empty placeholder, leaves the
        // shadow map as it was.
        if (!_enabled || _reusing || !__result || ___m_CreateEmptyShadowmap)
            return;
        try
        {
            _drawnCount++;
            var cameraData = renderingData.cameraData;
            var xr = cameraData.xr;
            var camera = cameraData.camera;
            var shadows = renderingData.shadowData;
            if (xr == null || camera == null || ___m_CascadeSlices.Length < Slices.Length ||
                ___m_CascadeSplitDistances.Length < Splits.Length)
            {
                Forget();
                return;
            }
            _drawn = SharedShadowPolicy.Record(xr.enabled, xr.singlePassEnabled, xr.multipassId, Time.frameCount,
                camera.GetInstanceID(), shadows.mainLightShadowCascadesCount, shadows.mainLightShadowmapWidth,
                shadows.mainLightShadowmapHeight);
            _pass = __instance;
            _renderer = cameraData.renderer;
            _texture = ___m_MainLightShadowmapTexture;
            Array.Copy(___m_CascadeSlices, Slices, Slices.Length);
            Array.Copy(___m_CascadeSplitDistances, Splits, Splits.Length);
            _border = ___m_CascadeBorder;
            _maxDistanceSq = ___m_MaxShadowDistanceSq;
            _targetWidth = ___renderTargetWidth;
            _targetHeight = ___renderTargetHeight;
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    // The pass asks for the shadow map as a cleared target. A reused map must
    // survive, so the pass is left without a target of its own, which is what
    // the render pipeline itself does when it has no shadow map to draw.
    private static void ConfigurePostfix(MainLightShadowCasterPass __instance)
    {
        if (_reusing && ReferenceEquals(__instance, _pass))
            __instance.ResetTarget();
    }

    private static void ExecutePrefix(MainLightShadowCasterPass __instance) =>
        _skipDraws = _reusing && ReferenceEquals(__instance, _pass);

    private static void ExecuteFinalizer() => _skipDraws = false;

    // Everything else the pass does while executing (keywords, the matrices
    // the scene shaders sample the map with) still has to happen per eye.
    private static bool RenderShadowSlicePrefix() => !_skipDraws;
}
