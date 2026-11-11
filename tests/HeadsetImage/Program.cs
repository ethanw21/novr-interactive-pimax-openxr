using System.Reflection;
using NOVR;
using NOVR.Compatibility;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR;

var checks = 0;
void Check(bool condition, string reason)
{
    checks++;
    if (!condition)
        throw new Exception(reason);
}
static void Call(OpaqueHeadsetImage target, string message) =>
    typeof(OpaqueHeadsetImage).GetMethod(message, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);

// Saved values are part of the user's configuration file.
Check(Enum.GetNames<HeadsetImageOpacity>().SequenceEqual(new[] { "Automatic", "Always", "Off" }) &&
      Enum.GetValues<HeadsetImageOpacity>().Select(value => (int)value).SequenceEqual(new[] { 0, 1, 2 }),
    "Saved opacity modes keep their names and values");
Check(default(HeadsetImageOpacity) == HeadsetImageOpacity.Automatic, "A new configuration uses the compatible default");
foreach (var mode in Enum.GetValues<HeadsetImageOpacity>())
    Check(HeadsetImagePolicy.Normalize((int)mode) == mode, "A valid saved mode survives: " + mode);
foreach (var invalid in new[] { -1, 3, 99, int.MinValue })
    Check(HeadsetImagePolicy.Normalize(invalid) == HeadsetImageOpacity.Automatic, "An unreadable mode cannot disable the fix");
Check(HeadsetImagePolicy.OpaqueLayerFlags == 0, "The opaque request carries neither source-alpha nor unpremultiplied-alpha bits");
Check(HeadsetImagePolicy.SubmitsOpaqueLayer(HeadsetImageOpacity.Automatic) && HeadsetImagePolicy.SubmitsOpaqueLayer(HeadsetImageOpacity.Always) &&
      !HeadsetImagePolicy.SubmitsOpaqueLayer(HeadsetImageOpacity.Off), "Only Off keeps the plugin's alpha-blended layer");
foreach (var name in new[] { "PimaxXR (Unofficial)", "Pimax OpenXR", "pimax", "PIMAX Play" })
    Check(HeadsetImagePolicy.WritesOpaqueAlpha(HeadsetImageOpacity.Automatic, name), "Automatic rewrites alpha for " + name);
foreach (var other in new[] { "SteamVR/OpenXR", "Oculus", "Windows Mixed Reality Runtime", "VirtualDesktopXR", "OpenXR", "", null })
{
    Check(!HeadsetImagePolicy.WritesOpaqueAlpha(HeadsetImageOpacity.Automatic, other), "Automatic adds no per-frame work for " + (other ?? "an unnamed runtime"));
    Check(HeadsetImagePolicy.WritesOpaqueAlpha(HeadsetImageOpacity.Always, other), "Always is an explicit override on any runtime");
    Check(!HeadsetImagePolicy.WritesOpaqueAlpha(HeadsetImageOpacity.Off, other), "Off never rewrites alpha");
}
Check(!HeadsetImagePolicy.WritesOpaqueAlpha(HeadsetImageOpacity.Off, "Pimax OpenXR"), "Off wins over runtime detection");

var flagRequests = new List<ulong>();
var flagClears = 0;
Exception? nativeFailure = null;
OpaqueHeadsetImage.SetSceneLayerFlags = flags =>
{
    if (nativeFailure != null)
        throw nativeFailure;
    flagRequests.Add(flags);
};
OpaqueHeadsetImage.ClearSceneLayerFlags = () =>
{
    if (nativeFailure != null)
        throw nativeFailure;
    flagClears++;
};
var setting = ModConfiguration.Instance.HeadsetImageOpacity;
var headset = new Camera();
var instrument = new Camera { targetTexture = new RenderTexture() };
var sceneView = new Camera { cameraType = CameraType.SceneView };
var desktopOnly = new Camera { stereoTargetEye = StereoTargetEyeMask.None };
XRDisplaySubsystem StereoDisplay(params int[][] slices)
{
    var display = new XRDisplaySubsystem();
    for (var index = 0; index < slices.Length; index++)
        display.Passes.Add(new XRDisplaySubsystem.XRRenderPass
        {
            renderTarget = new RenderTargetIdentifier("eye" + index),
            renderTargetDesc = new RenderTextureDescriptor { width = 3840, height = 3552, msaaSamples = 1, graphicsFormat = "R8G8B8A8_SRGB" },
            Slices = slices[index]
        });
    return display;
}
void Reset(string runtime, HeadsetImageOpacity mode, XRDisplaySubsystem? display)
{
    flagRequests.Clear();
    flagClears = 0;
    nativeFailure = null;
    Debug.Logs.Clear();
    Debug.Warnings.Clear();
    XRSettings.enabled = false;
    OpenXRRuntime.Name = runtime;
    OpenXRRuntime.Throw = false;
    Shader.Available = true;
    setting.Value = mode;
    SubsystemManager.Subsystems.Clear();
    if (display != null)
        SubsystemManager.Subsystems.Add(display);
}
OpaqueHeadsetImage Start()
{
    var component = new OpaqueHeadsetImage();
    Call(component, "OnEnable");
    return component;
}
void Stop(OpaqueHeadsetImage component)
{
    Call(component, "OnDisable");
    Call(component, "OnDestroy");
    Check(RenderPipelineManager.Subscribers == 0, "A disabled component leaves no render callback behind");
}
ScriptableRenderContext Frame(params Camera[] cameras)
{
    var context = new ScriptableRenderContext();
    RenderPipelineManager.EndFrame(context, cameras);
    return context;
}

// Pimax: multi-pass with one texture array, the layout Unity's OpenXR plugin prefers.
var pimaxDisplay = StereoDisplay(new[] { 0 }, new[] { 1 });
pimaxDisplay.Passes[1] = pimaxDisplay.Passes[1] with { renderTarget = new RenderTargetIdentifier("eye0") };
Reset("Pimax OpenXR", HeadsetImageOpacity.Automatic, pimaxDisplay);
var pimax = Start();
Check(flagRequests.SequenceEqual(new ulong[] { 0 }) && flagClears == 0, "The opaque layer is requested before OpenXR starts");
Check(Debug.Logs.Count == 0 && Debug.Warnings.Count == 0 && OpaqueHeadsetImage.Status == "VR runtime is not active.", "Nothing is reported while flat");
Check(Frame(headset).Executed.Count == 0 && Shader.Requests.Count == 0, "No eye texture is touched before the runtime is active");
Call(pimax, "Update");
Check(flagRequests.Count == 1, "An unchanged state makes no further native calls");
XRSettings.enabled = true;
Call(pimax, "Update");
Call(pimax, "Update");
Check(flagRequests.SequenceEqual(new ulong[] { 0, 0 }), "The request is repeated once for the started session");
Check(Debug.Logs.Count == 1 && Debug.Logs[0] == "[NOVR] Headset image: runtime=Pimax OpenXR; mode=Automatic; opaque layer=requested; alpha rewrite=on.",
    "One startup line records runtime, layer request and alpha rewrite");
foreach (var cameras in new[] { Array.Empty<Camera>(), new[] { instrument }, new[] { sceneView }, new[] { desktopOnly }, new[] { instrument, sceneView, desktopOnly } })
    Check(Frame(cameras).Executed.Count == 0, "Renders that never reach the headset leave the eye textures alone");
var frame = Frame(instrument, headset);
Check(frame.Executed.Count == 1 && frame.Submits == 1 && CommandBufferPool.Outstanding == 0, "A headset frame submits one pooled command buffer");
var command = frame.Executed[0];
Check(command.Calls.SequenceEqual(new[]
{
    "clip-space",
    "target eye0 mip 0 face Unknown slice 0", "draw",
    "target eye0 mip 0 face Unknown slice 1", "draw"
}), "Each eye slice is bound and covered exactly once: " + string.Join(" | ", command.Calls));
var material = command.Material!;
Check(Shader.Requests.SequenceEqual(new[] { "Hidden/Internal-Colored" }), "Only a shader shipped with every player is requested");
Check(material.Colors["_Color"] == new Color(0, 0, 0, 1) && material.Floats["_SrcBlend"] == 1 && material.Floats["_DstBlend"] == 6,
    "Blending keeps RGB and replaces alpha with one");
Check(material.Floats["_ZWrite"] == 0 && material.Floats["_ZTest"] == 8 && material.Floats["_Cull"] == 0, "The rewrite ignores and preserves scene depth");
var mesh = command.Mesh!;
Check(mesh.vertices.SequenceEqual(new[] { new Vector3(-1, -1, 0), new Vector3(-1, 3, 0), new Vector3(3, -1, 0) }) &&
      mesh.colors.All(color => color == Color.white) && mesh.colors.Length == 3 && mesh.triangles.SequenceEqual(new[] { 0, 1, 2 }),
    "One white clip-space triangle covers the whole target");
Check(Debug.Logs.Count == 2 && Debug.Logs[1].Contains("3840x3552") && Debug.Logs[1].Contains("R8G8B8A8_SRGB"), "The eye target is described once");
var again = Frame(headset);
Check(again.Executed.Count == 1 && again.Executed[0].Material == material && again.Executed[0].Mesh == mesh && Material.Created == 1 && Debug.Logs.Count == 2,
    "Later frames reuse the material and mesh without logging");

// The user can compare live.
setting.Value = HeadsetImageOpacity.Off;
Call(pimax, "Update");
Check(flagClears == 1 && Frame(headset).Executed.Count == 0, "Off withdraws the layer request and stops the rewrite at once");
Check(Debug.Logs[^1].EndsWith("mode=Off; opaque layer=plugin default; alpha rewrite=off."), "The change is recorded");
setting.Value = HeadsetImageOpacity.Automatic;
Call(pimax, "Update");
Check(flagRequests.Count == 3 && Frame(headset).Executed.Count == 1, "Automatic restores both protections");
setting.Value = (HeadsetImageOpacity)99;
Call(pimax, "Update");
Check(flagRequests.Count == 3 && flagClears == 1 && Frame(headset).Executed.Count == 1, "A corrupt saved value behaves as Automatic");
setting.Value = HeadsetImageOpacity.Automatic;

// Failures never reach the render loop.
pimaxDisplay.ThrowOnPass = true;
Check(Frame(headset).Executed.Count == 0 && Frame(headset).Executed.Count == 0 && Debug.Warnings.Count == 0, "A brief render-pass failure is tolerated quietly");
pimaxDisplay.ThrowOnPass = false;
Check(Frame(headset).Executed.Count == 1, "The rewrite resumes after a transient failure");
pimaxDisplay.ThrowOnPass = true;
for (var attempt = 0; attempt < 6; attempt++)
    Frame(headset);
Check(Debug.Warnings.Count == 1 && Debug.Warnings[0].StartsWith("[NOVR] Headset image alpha rewrite stopped: InvalidOperationException"),
    "A persistent failure is reported once and then stops");
pimaxDisplay.ThrowOnPass = false;
Check(Frame(headset).Executed.Count == 0 && CommandBufferPool.Outstanding == 0, "A stopped rewrite stays stopped and leaks no command buffer");
XRSettings.enabled = false;
Call(pimax, "Update");
XRSettings.enabled = true;
Call(pimax, "Update");
Check(Frame(headset).Executed.Count == 1, "A new VR session earns a fresh attempt");
pimaxDisplay.running = false;
Check(Frame(headset).Executed.Count == 0, "A stopped display is not drawn to");
pimaxDisplay.running = true;
pimaxDisplay.Passes.Clear();
Check(Frame(headset).Executed.Count == 0 && Debug.Warnings.Count == 1, "A display without render passes is skipped without a fault");
Stop(pimax);
Check(material.Destroyed && mesh.Destroyed && Frame(headset).Executed.Count == 0, "Teardown releases the material and mesh");

// SteamVR keeps its current per-frame cost.
Reset("SteamVR/OpenXR", HeadsetImageOpacity.Automatic, StereoDisplay(new[] { 0 }, new[] { 0 }));
var steam = Start();
XRSettings.enabled = true;
Call(steam, "Update");
Check(flagRequests.SequenceEqual(new ulong[] { 0, 0 }) && Frame(headset).Executed.Count == 0,
    "Other runtimes get the opaque layer request but no per-frame work");
Check(Debug.Logs.Count == 1 && Debug.Logs[0].EndsWith("opaque layer=requested; alpha rewrite=off."), "The log shows why nothing is rewritten");
setting.Value = HeadsetImageOpacity.Always;
Call(steam, "Update");
var forced = Frame(headset);
Check(forced.Executed.Count == 1 && forced.Executed[0].Calls.SequenceEqual(new[]
{
    "clip-space", "target eye0 mip 0 face Unknown slice 0", "draw", "target eye1 mip 0 face Unknown slice 0", "draw"
}), "Always rewrites separate eye textures on any runtime");
Shader.Available = true;
Stop(steam);

// A missing built-in shader is a reported, non-fatal limitation.
Reset("Pimax OpenXR", HeadsetImageOpacity.Automatic, StereoDisplay(new[] { 0 }, new[] { 0 }));
Shader.Available = false;
var noShader = Start();
XRSettings.enabled = true;
Call(noShader, "Update");
for (var attempt = 0; attempt < 5; attempt++)
    Check(Frame(headset).Executed.Count == 0, "Nothing is drawn without the shader");
Check(Debug.Warnings.Count == 1 && Debug.Warnings[0].Contains("Built-in colour shader is missing") && CommandBufferPool.Outstanding == 0,
    "The missing shader is reported once; the layer request remains in force");
Stop(noShader);

// The plugin may be unloadable until OpenXR starts, or lack the entry point.
Reset("Pimax OpenXR", HeadsetImageOpacity.Automatic, StereoDisplay(new[] { 0 }, new[] { 0 }));
nativeFailure = new DllNotFoundException("UnityOpenXR");
var late = Start();
Check(flagRequests.Count == 0 && Debug.Warnings.Count == 0, "An early load failure is silent");
nativeFailure = null;
XRSettings.enabled = true;
Call(late, "Update");
Check(flagRequests.SequenceEqual(new ulong[] { 0 }) && Debug.Warnings.Count == 0 && Debug.Logs[0].Contains("opaque layer=requested"),
    "The request succeeds once the plugin is loaded");
setting.Value = HeadsetImageOpacity.Off;
nativeFailure = new EntryPointNotFoundException("ext_composition_layers_ClearDefaultSceneLayerFlags");
Call(late, "Update");
Check(Debug.Warnings.Count == 1 && Debug.Warnings[0] == "[NOVR] The OpenXR plugin did not accept scene-layer flags: EntryPointNotFoundException" &&
      Debug.Logs[^1].Contains("opaque layer=unavailable"), "A final plugin failure is reported without interrupting startup");
Stop(late);

Reset("Pimax OpenXR", HeadsetImageOpacity.Automatic, StereoDisplay(new[] { 0 }, new[] { 0 }));
nativeFailure = new EntryPointNotFoundException("ext_composition_layers_SetDefaultSceneLayerFlags");
var oldPlugin = Start();
XRSettings.enabled = true;
Call(oldPlugin, "Update");
Check(Debug.Warnings.Count == 1 && Debug.Logs[0].EndsWith("opaque layer=unavailable; alpha rewrite=on.") && Frame(headset).Executed.Count == 1,
    "Without flag control the alpha rewrite still protects a Pimax headset");
Stop(oldPlugin);

// Off is inert from the first frame, and an unnamed runtime is not guessed at.
Reset("Pimax OpenXR", HeadsetImageOpacity.Off, StereoDisplay(new[] { 0 }, new[] { 0 }));
var off = Start();
XRSettings.enabled = true;
Call(off, "Update");
Check(flagRequests.Count == 0 && flagClears == 0 && Frame(headset).Executed.Count == 0 && Debug.Warnings.Count == 0,
    "Off makes no native call and draws nothing");
Stop(off);
Reset("", HeadsetImageOpacity.Automatic, StereoDisplay(new[] { 0 }, new[] { 0 }));
OpenXRRuntime.Throw = true;
var unnamed = Start();
XRSettings.enabled = true;
Call(unnamed, "Update");
Check(Debug.Logs[0].Contains("runtime=OpenXR;") && Frame(headset).Executed.Count == 0 && flagRequests.Count == 2,
    "An unreadable runtime name keeps the layer request and skips the rewrite");
Stop(unnamed);

Console.WriteLine($"{checks} source-linked headset image opacity checks passed (render, XR display and OpenXR plugin doubles; no headset/runtime launched).");
