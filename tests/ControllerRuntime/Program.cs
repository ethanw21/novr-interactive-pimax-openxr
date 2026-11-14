using NOVR.MotionControls;
using UnityEngine;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.Features.Interactions;
using Valve.OpenXR.Utils;

var assertions = 0;
void yes(bool value) { assertions++; if (!value) throw new Exception("Controller runtime assertion " + assertions); }
void near(float a, float b) => yes(Math.Abs(a - b) < .0001f);
TrackingAvailabilityChecks.Run(yes);
DeviceState tracking() => new DeviceState().Set(CommonUsages.isTracked, true)
    .Set(CommonUsages.trackingState, InputTrackingState.Position | InputTrackingState.Rotation)
    .Set(CommonUsages.devicePosition, new Vector3(.2f, 1.1f, -.3f)).Set(CommonUsages.deviceRotation, Quaternion.identity);
void install(bool left, XRController controller, DeviceState state)
{
    if (left)
        XRController.leftHand = controller;
    else
        XRController.rightHand = controller;
    InputDevices.Devices[left ? XRNode.LeftHand : XRNode.RightHand] = new InputDevice(state);
    Time.frameCount++;
}
void button(XRController controller, string name, bool pressed = true) => controller.Controls[name] = new ButtonControl { Value = pressed ? 1 : 0 };
InputDevices.Devices[XRNode.Head] = new InputDevice(tracking());

// Real source sampling, with only runtime I/O replaced. Names match the bundled
// OpenXR action maps, including generated layouts derived from their classes.
foreach (var controller in new XRController[] { new DerivedTouch(), new MetaQuestTouchPlusControllerProfile.QuestTouchPlusController(),
             new MetaQuestTouchProControllerProfile.QuestProTouchController() })
    foreach (var left in new[] { true, false })
    {
        foreach (var name in new[] { "primaryButton", "secondaryButton", "thumbstickClicked", "menu", "primaryTouched", "secondaryTouched", "triggerTouched" })
            button(controller, name);
        controller.Controls["grip"] = new AxisControl { Value = .72f };
        controller.Controls["trigger"] = new AxisControl { Value = .43f };
        controller.Controls["thumbstick"] = new Vector2Control { Value = new Vector2(.2f, -.6f) };
        install(left, controller, tracking());
        var sample = TrackedControllers.Read(left);
        yes(sample.Valid && sample.Profile == ControllerHardwareProfile.Touch && !sample.NativeFrame);
        yes(TrackedControllers.Profile(left) == ControllerHardwareProfile.Touch && !TrackedControllers.NativeFrame(left));
        yes(sample.Primary && sample.Secondary && sample.StickClick && sample.HasAxis);
        yes(sample.Menu == left && !sample.Shoulder && !sample.Top && !sample.Outside);
        near(sample.Grip, .72f);
        near(sample.Trigger, .43f);
        near(sample.Axis.y, -.6f);
        yes(sample.PrimaryTouched && sample.SecondaryTouched && !sample.TopTouched && !sample.OutsideTouched && sample.TriggerTouched && sample.HasThumbTouch && sample.HasTriggerTouch);
    }

var touch = new DerivedTouch();
foreach (var name in new[] { "primaryButton", "secondaryButton", "thumbstickClicked", "menu", "gripPressed", "triggerPressed" }) button(touch, name);
touch.Controls["grip"] = new AxisControl { Value = .9f };
touch.Controls["trigger"] = new AxisControl { Value = .9f };
var common = tracking().Set(CommonUsages.grip, .27f).Set(CommonUsages.trigger, .36f)
    .Set(CommonUsages.gripButton, false).Set(CommonUsages.primaryButton, false).Set(CommonUsages.secondaryButton, false)
    .Set(CommonUsages.primary2DAxisClick, false).Set(CommonUsages.menuButton, false);
install(true, touch, common);
var legacy = TrackedControllers.Read(true);
yes(!legacy.Primary && !legacy.Secondary && !legacy.StickClick && !legacy.Menu);
near(legacy.Grip, .27f);
near(legacy.Trigger, .36f);
// A coherent sample stays unchanged within a frame, then sees the next input.
common.Set(CommonUsages.trigger, .81f);
near(TrackedControllers.Read(true).Trigger, .36f);
Time.frameCount++;
near(TrackedControllers.Read(true).Trigger, .81f);

touch.Controls.Clear();
button(touch, "primaryTouched");
button(touch, "thumbstickTouched");
install(true, touch, tracking());
var presence = TrackedControllers.Read(true);
yes(presence.PrimaryTouched && presence.ThumbstickTouched && !presence.Primary && !presence.StickClick);
near(presence.Grip, 0);
near(presence.Trigger, 0);
button(touch, "gripPressed");
button(touch, "triggerPressed");
Time.frameCount++;
var clicks = TrackedControllers.Read(true);
near(clicks.Grip, 1);
near(clicks.Trigger, 1);

var frame = new DerivedFrame();
foreach (var name in new[] { "faceButtonBottom", "faceButtonInside", "faceButtonTop", "faceButtonOutside", "shoulder", "menu", "thumbstickClicked", "gripPressed", "faceButtonBottomTouched", "faceButtonInsideTouched", "faceButtonTopTouched", "faceButtonOutsideTouched" }) button(frame, name);
var frameState = tracking().Set(CommonUsages.primaryButton, false).Set(CommonUsages.secondaryButton, false)
    .Set(CommonUsages.primary2DAxisClick, false).Set(CommonUsages.menuButton, false).Set(CommonUsages.grip, .1f).Set(CommonUsages.gripButton, false);
install(false, frame, frameState);
var frameSample = TrackedControllers.Read(false);
yes(frameSample.NativeFrame && frameSample.Profile == ControllerHardwareProfile.SteamFrame);
yes(frameSample.Primary && frameSample.Secondary && frameSample.Top && frameSample.Outside && frameSample.Shoulder && frameSample.Menu && frameSample.StickClick);
near(frameSample.Grip, 1);
yes(frameSample.PrimaryTouched && frameSample.SecondaryTouched && frameSample.TopTouched && frameSample.OutsideTouched && frameSample.ButtonTouched);

// Aim validity is explicit so free gloves can use a stable hardware fallback.
yes(!frameSample.HasAimPose);
var pointer = new UnityEngine.XR.OpenXR.Input.PoseControl();
pointer.isTracked.Value = 1;
pointer.trackingState.Value = 3;
pointer.position.Value = new Vector3(.21f, 1.12f, -.25f);
pointer.rotation.Value = new Quaternion(.3f, 0, 0, .9539392f);
frame.Controls["pointer"] = pointer;
Time.frameCount++;
var aimedFrame = TrackedControllers.Read(false);
yes(aimedFrame.HasAimPose && aimedFrame.Valid);
near(aimedFrame.AimRotation.x, .3f);
pointer.isTracked.Value = 0;
Time.frameCount++;
yes(!TrackedControllers.Read(false).HasAimPose);
pointer.isTracked.Value = 1;
pointer.rotation.Value = new Quaternion(float.NaN, 0, 0, 1);
Time.frameCount++;
yes(!TrackedControllers.Read(false).HasAimPose && TrackedControllers.Read(false).Valid);

// Valve Index: names match the bundled ValveIndexControllerProfile action map.
// Its generated layout derives from the registered class, like the others.
var trackpadForce = new InputFeatureUsage<float>("Secondary2DAxisForce");
foreach (var left in new[] { true, false })
{
    var index = new DerivedIndex();
    foreach (var name in new[] { "primaryButton", "secondaryButton", "thumbstickClicked", "system", "primaryTouched", "secondaryTouched", "thumbstickTouched", "triggerTouched" })
        button(index, name);
    index.Controls["grip"] = new AxisControl { Value = .8f };
    index.Controls["trigger"] = new AxisControl { Value = .43f };
    index.Controls["thumbstick"] = new Vector2Control { Value = new Vector2(.2f, -.6f) };
    var force = new AxisControl();
    index.Controls["trackpadForce"] = force;
    // Both runtimes keep the system button; a reported press must not reach the mod.
    install(left, index, tracking().Set(CommonUsages.menuButton, true));
    var sample = TrackedControllers.Read(left);
    yes(sample.Valid && sample.Profile == ControllerHardwareProfile.Index && !sample.NativeFrame);
    yes(TrackedControllers.Profile(left) == ControllerHardwareProfile.Index && !TrackedControllers.NativeFrame(left));
    yes(sample.Primary && sample.Secondary && sample.StickClick && sample.HasAxis);
    yes(!sample.Menu && !sample.Shoulder && !sample.Top && !sample.Outside);
    yes(!TrackedControllers.IsApplicationMenuAvailable(ControllerHardwareProfile.Index, left));
    near(sample.Grip, .8f);
    near(sample.Trigger, .43f);
    near(sample.Axis.y, -.6f);
    yes(sample.PrimaryTouched && sample.SecondaryTouched && sample.ThumbstickTouched && sample.TriggerTouched && sample.HasThumbTouch && sample.HasTriggerTouch);
    yes(!sample.Trackpad && !sample.TrackpadTouched && !sample.ThumbrestTouched);

    // The profile has trackpad force and touch but no click. A resting thumb
    // touches without pressing; a press holds through a slightly lighter force.
    ButtonControl padTouch = new() { Value = 1 };
    index.Controls["trackpadTouched"] = padTouch;
    float[] forces = { .2f, .39f, .4f, .9f, .31f, .29f, .35f, .45f, 0 };
    bool[] pressed = { false, false, true, true, true, false, false, true, false };
    for (var i = 0; i < forces.Length; i++)
    {
        force.Value = forces[i];
        Time.frameCount++;
        sample = TrackedControllers.Read(left);
        yes(sample.Trackpad == pressed[i] && sample.TrackpadTouched && sample.ThumbrestTouched);
        yes(!sample.Menu && sample.Primary);
    }
    padTouch.Value = 0;
    Time.frameCount++;
    yes(!TrackedControllers.Read(left).TrackpadTouched && !TrackedControllers.Read(left).ThumbrestTouched);

    // The configured press point applies at once, and tracking loss drops a held press.
    TrackedControllers.TrackpadPressForce = .2f;
    force.Value = .25f;
    Time.frameCount++;
    yes(TrackedControllers.Read(left).Trackpad);
    TrackedControllers.TrackpadPressForce = TrackedControllers.DefaultTrackpadPressForce;
    force.Value = .35f;
    Time.frameCount++;
    yes(TrackedControllers.Read(left).Trackpad);
    OpenXRUtility.IsSessionFocused = false;
    Time.frameCount++;
    yes(!TrackedControllers.Read(left).Valid && !TrackedControllers.Read(left).Trackpad);
    OpenXRUtility.IsSessionFocused = true;
    Time.frameCount++;
    yes(TrackedControllers.Read(left).Valid && !TrackedControllers.Read(left).Trackpad);

    // The legacy feature bridge is preferred when it reports the same force.
    index.Controls.Remove("trackpadForce");
    install(left, index, tracking().Set(trackpadForce, .7f));
    yes(TrackedControllers.Read(left).Trackpad);
    install(left, index, tracking());
    yes(!TrackedControllers.Read(left).Trackpad);

    // Grip force is separate from the grip value that holds a handle. A firm
    // squeeze is a button with the same hold-through margin as a trackpad press.
    var squeezeForce = new AxisControl();
    index.Controls["gripForce"] = squeezeForce;
    float[] squeezes = { 0, .3f, .49f, .5f, .9f, .4f, .37f, 0 };
    bool[] squeezed = { false, false, false, true, true, true, false, false };
    for (var i = 0; i < squeezes.Length; i++)
    {
        squeezeForce.Value = squeezes[i];
        Time.frameCount++;
        sample = TrackedControllers.Read(left);
        yes(sample.GripSqueeze == squeezed[i] && !sample.Trackpad);
        near(sample.GripForce, squeezes[i]);
        near(sample.Grip, .8f);
    }
    TrackedControllers.GripSqueezeForce = .2f;
    squeezeForce.Value = .25f;
    Time.frameCount++;
    yes(TrackedControllers.Read(left).GripSqueeze);
    TrackedControllers.GripSqueezeForce = TrackedControllers.DefaultGripSqueezeForce;
    OpenXRUtility.IsSessionFocused = false;
    squeezeForce.Value = .45f;
    Time.frameCount++;
    yes(!TrackedControllers.Read(left).GripSqueeze);
    OpenXRUtility.IsSessionFocused = true;
    Time.frameCount++;
    yes(TrackedControllers.Read(left).Valid && !TrackedControllers.Read(left).GripSqueeze);
    squeezeForce.Value = float.NaN;
    Time.frameCount++;
    yes(!TrackedControllers.Read(left).GripSqueeze && TrackedControllers.Read(left).GripForce == 0);
    index.Controls.Remove("gripForce");
    install(left, index, tracking().Set(new InputFeatureUsage<float>("GripForce"), .7f));
    yes(TrackedControllers.Read(left).GripSqueeze);
    install(left, index, tracking());
    yes(!TrackedControllers.Read(left).GripSqueeze);

    // Trackpad position: reported only under a touching thumb. A quick stroke
    // along the pad is a swipe, held for a moment like a button tap.
    var padAxis = new Vector2Control();
    index.Controls["trackpad"] = padAxis;
    var clock = 100f + (left ? 0 : 50);
    ControllerSample pad(float x, float y, float seconds = .011f)
    {
        padAxis.Value = new Vector2(x, y);
        clock += seconds;
        Time.unscaledTime = clock;
        Time.frameCount++;
        return TrackedControllers.Read(left);
    }
    sample = pad(.4f, -.6f);
    yes(!sample.HasTrackpadAxis && sample.TrackpadAxis == Vector2.zero && sample.TrackpadScroll == 0);
    padTouch.Value = 1;
    sample = pad(.1f, -.6f);
    yes(sample.HasTrackpadAxis && sample.TrackpadTouched && !sample.SwipeUp && !sample.SwipeDown);
    near(sample.TrackpadAxis.x, .1f);
    near(sample.TrackpadAxis.y, -.6f);
    pad(.1f, -.6f, .06f);
    var swipedUp = false;
    foreach (var y in new[] { -.45f, -.3f, -.15f, 0f, .15f, .3f })
    {
        sample = pad(.1f, y);
        swipedUp |= sample.SwipeUp;
        yes(!sample.SwipeDown && !sample.Trackpad);
        near(sample.TrackpadScroll, .15f);
    }
    yes(swipedUp && sample.SwipeUp);
    yes(pad(.1f, .3f, .05f).SwipeUp);
    yes(!pad(.1f, .3f, .2f).SwipeUp);
    // The return stroke of the same touch is not a second swipe.
    foreach (var y in new[] { .15f, 0f, -.15f, -.3f, -.45f, -.6f })
        yes(!pad(.1f, y).SwipeDown);
    padTouch.Value = 0;
    yes(!pad(0, 0).HasTrackpadAxis);
    padTouch.Value = 1;
    pad(0, .6f);
    pad(0, .6f, .06f);
    var swipedDown = false;
    foreach (var y in new[] { .45f, .3f, .15f, 0f, -.15f, -.3f })
    {
        sample = pad(0, y);
        swipedDown |= sample.SwipeDown;
        yes(!sample.SwipeUp);
    }
    yes(swipedDown);
    // Losing tracking drops the tap at once.
    OpenXRUtility.IsSessionFocused = false;
    yes(!pad(0, -.3f).SwipeDown);
    OpenXRUtility.IsSessionFocused = true;
    yes(!pad(0, -.3f).SwipeDown);
    // The legacy feature bridge reports the same position.
    index.Controls.Remove("trackpad");
    install(left, index, tracking().Set(CommonUsages.secondary2DAxis, new Vector2(-.2f, .7f)));
    near(TrackedControllers.Read(left).TrackpadAxis.y, .7f);
    install(left, index, tracking());
    yes(!TrackedControllers.Read(left).HasTrackpadAxis);
    padTouch.Value = 0;

    // Finger joints come from the runtime's hand tracking and only for Index.
    var hand = left ? 0 : 1;
    NovrHandJointsFeature.Hands[hand] = IndexInputChecks.Hand(.25f, .5f, .75f, 1);
    NovrHandJointsFeature.Reads = 0;
    Time.frameCount++;
    sample = TrackedControllers.Read(left);
    yes(sample.HasFingers && NovrHandJointsFeature.Reads == 1);
    near(sample.Fingers.Index, .25f);
    near(sample.Fingers.Middle, .5f);
    near(sample.Fingers.Ring, .75f);
    near(sample.Fingers.Little, 1);
    yes(TrackedControllers.Read(left).HasFingers && NovrHandJointsFeature.Reads == 1);
    TrackedControllers.FingerTracking = false;
    Time.frameCount++;
    yes(!TrackedControllers.Read(left).HasFingers && NovrHandJointsFeature.Reads == 1);
    TrackedControllers.FingerTracking = true;
    NovrHandJointsFeature.Hands[hand] = null;
    Time.frameCount++;
    sample = TrackedControllers.Read(left);
    yes(!sample.HasFingers && sample.Fingers.Index == 0 && sample.Valid);
    // Joints stacked on one point carry no bend, so the hand is not tracked.
    NovrHandJointsFeature.Hands[hand] = new Vector3[FingerCurlMath.JointCount];
    Time.frameCount++;
    yes(!TrackedControllers.Read(left).HasFingers);
    NovrHandJointsFeature.Hands[hand] = IndexInputChecks.Hand(1, 1, 1, 1);

    // Other controllers never report a trackpad button, whatever their layout exposes.
    var padded = new DerivedTouch();
    padded.Controls["trackpadForce"] = new AxisControl { Value = 1 };
    padded.Controls["gripForce"] = new AxisControl { Value = 1 };
    padded.Controls["trackpad"] = new Vector2Control { Value = new Vector2(0, 1) };
    button(padded, "trackpadTouched");
    NovrHandJointsFeature.Reads = 0;
    install(left, padded, tracking().Set(trackpadForce, 1f));
    sample = TrackedControllers.Read(left);
    yes(!sample.Trackpad && !sample.TrackpadTouched);
    yes(!sample.GripSqueeze && sample.GripForce == 0 && !sample.HasTrackpadAxis && !sample.SwipeUp && !sample.HasFingers && NovrHandJointsFeature.Reads == 0);
    NovrHandJointsFeature.Hands[hand] = null;
}
yes(!TrackedControllers.ForcePressed(false, float.NaN, .4f) && !TrackedControllers.ForcePressed(true, float.NaN, .4f));
yes(!TrackedControllers.ForcePressed(false, 1, 0) && !TrackedControllers.ForcePressed(false, 1, float.NaN));
IndexInputChecks.RunSwipes(yes);
yes(TrackedControllers.IsApplicationMenuAvailable(ControllerHardwareProfile.Touch, true) && !TrackedControllers.IsApplicationMenuAvailable(ControllerHardwareProfile.Touch, false));
yes(TrackedControllers.IsApplicationMenuAvailable(ControllerHardwareProfile.SteamFrame, false) && TrackedControllers.IsApplicationMenuAvailable(ControllerHardwareProfile.Other, false));
install(false, frame, frameState);

touch.Controls.Clear();
var invalid = tracking().Set(CommonUsages.trigger, float.NaN);
install(true, touch, invalid);
yes(!TrackedControllers.Read(true).Valid);
touch.Controls["trigger"] = new AxisControl { Value = .25f };
Time.frameCount++;
yes(TrackedControllers.Read(true).Valid);
near(TrackedControllers.Read(true).Trigger, .25f);
OpenXRUtility.IsSessionFocused = false;
Time.frameCount++;
yes(!TrackedControllers.Read(true).Valid);
OpenXRUtility.IsSessionFocused = true;
XRSettings.enabled = false;
Time.frameCount++;
yes(!TrackedControllers.Read(true).Valid);
XRSettings.enabled = true;
invalid.Set(CommonUsages.trackingState, InputTrackingState.Rotation);
Time.frameCount++;
yes(!TrackedControllers.Read(true).Valid);
XRController.leftHand = new XRController();
yes(TrackedControllers.Profile(true) == ControllerHardwareProfile.Other);
XRController.leftHand = null;
yes(TrackedControllers.Profile(true) == ControllerHardwareProfile.Unknown);

var settings = new OpenXRSettings();
settings.EnableNovrControllerProfiles();
var existingGaze = settings.features.Single(f => f is EyeGazeInteraction);
yes(existingGaze.enabled && !existingGaze.required && existingGaze.openxrExtensionStrings == EyeGazeInteraction.extensionString);
var existingFrame = settings.features.Single(f => f is SteamFrameControllerProfile);
settings.EnableNovrControllerProfiles();
yes(settings.features.Count(f => f is SteamFrameControllerProfile) == 1 && settings.features.Contains(existingFrame));
yes(settings.features.Count(f => f is OculusTouchControllerProfile) == 1);
// Index is a core OpenXR 1.0 profile: registered without an extension guard,
// so runtimes other than SteamVR can offer it too.
var indexFeature = settings.features.Single(f => f is ValveIndexControllerProfile);
yes(indexFeature.enabled && string.IsNullOrEmpty(indexFeature.openxrExtensionStrings));
yes(settings.features.Single(f => f is MetaQuestTouchPlusControllerProfile).openxrExtensionStrings == "XR_META_touch_controller_plus");
yes(settings.features.Single(f => f is MetaQuestTouchProControllerProfile).openxrExtensionStrings == "XR_FB_touch_controller_pro");
yes(existingFrame.openxrExtensionStrings == "XR_VALVE_frame_controller_interaction" && settings.features.All(f => f.enabled));
// Hand joints are an optional extension feature, added only when wanted.
var profileCount = settings.features.Length;
settings.EnableNovrHandJoints(false);
yes(settings.features.Length == profileCount && !settings.features.Any(f => f is NovrHandJointsFeature));
settings.EnableNovrHandJoints(true);
settings.EnableNovrHandJoints(true);
var joints = settings.features.Single(f => f is NovrHandJointsFeature);
yes(joints.enabled && !joints.required && joints.openxrExtensionStrings == NovrHandJointsFeature.extensionStrings && settings.features.Length == profileCount + 1);
settings.EnableNovrHandJoints(false);
yes(!joints.enabled && settings.features.Contains(joints));
settings.EnableNovrControllerProfiles();
yes(settings.features.Contains(joints) && !joints.enabled);
yes(settings.features.Count(f => f is EyeGazeInteraction) == 1 && settings.features.Contains(existingGaze));

// Production report updates once at startup, then only while its page is open.
var performance = new PcVrCapabilities();
var updatePerformance = typeof(PcVrCapabilities).GetMethod("Update", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
void performanceTick(float time) { Time.unscaledTime = time; updatePerformance.Invoke(performance, null); }
Debug.Logs.Clear();
Debug.Warnings.Clear();
performanceTick(0);
yes(PcVrCapabilities.Report.Contains("SteamVR") && PcVrCapabilities.Report.Contains("Direct3D11"));
yes(PcVrCapabilities.Report.Contains("3028 × 3028 per eye") && PcVrCapabilities.Report.Contains("1920 × 1080"));
yes(!PcVrCapabilities.Report.Contains("gaze", StringComparison.OrdinalIgnoreCase) && !PcVrCapabilities.Report.Contains("foveat", StringComparison.OrdinalIgnoreCase));
yes(Debug.Logs.Count == 1 && OpenXRRuntime.NameReads == 1);
XRSettings.eyeTextureWidth = 2400;
Screen.width = 1600;
performanceTick(2);
yes(OpenXRRuntime.NameReads == 1 && PcVrCapabilities.Report.Contains("3028"));
NOVR.VrUi.WristMenu.IsPerformancePage = true;
performanceTick(3);
yes(PcVrCapabilities.Report.Contains("2400 × 3028") && PcVrCapabilities.Report.Contains("1600 × 1080"));
yes(Debug.Logs.Count == 1 && OpenXRRuntime.NameReads == 2);
performanceTick(3.5f);
yes(OpenXRRuntime.NameReads == 2);
XRSettings.enabled = false;
performanceTick(4);
yes(PcVrCapabilities.Report == "VR runtime is not active.");
NOVR.VrUi.WristMenu.IsPerformancePage = false;
XRSettings.enabled = true;
OpenXRRuntime.Fail = true;
performanceTick(5);
performanceTick(6);
yes(Debug.Warnings.Count == 1 && PcVrCapabilities.Report.Contains("not available yet"));
OpenXRRuntime.Fail = false;
XRSettings.eyeTextureWidth = 0;
performanceTick(7);
yes(Debug.Logs.Count == 2 && PcVrCapabilities.Report.Contains("Waiting for eye textures"));
Console.WriteLine($"{assertions} controller runtime/profile assertions passed (source linked; no headset or game). ");

sealed class DerivedTouch : OculusTouchControllerProfile.OculusTouchController
{
}
sealed class DerivedFrame : SteamFrameControllerProfile.SteamFrameController
{
}
sealed class DerivedIndex : ValveIndexControllerProfile.ValveIndexController
{
}
