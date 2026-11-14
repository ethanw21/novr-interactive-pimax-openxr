using UnityEngine;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.Features.Interactions;
using CommonUsages = UnityEngine.XR.CommonUsages;

namespace NOVR.MotionControls;

internal struct ControllerSample
{
    public bool Valid, HasAxis, HasAimPose, StickClick, Primary, Secondary, Menu, Shoulder, Top, Outside, Trackpad, NativeFrame;
    // Valve Index only. GripSqueeze is a firm squeeze past the configured force;
    // SwipeUp and SwipeDown read as held for a moment after a thumb swipe.
    public bool GripSqueeze, SwipeUp, SwipeDown, HasTrackpadAxis, HasFingers;
    // Presence is visual-only and never aliases a button press or flight action.
    public bool HasThumbTouch, HasTriggerTouch, ThumbstickTouched, ButtonTouched, PrimaryTouched, SecondaryTouched, TopTouched, OutsideTouched, ThumbrestTouched, TrackpadTouched, TriggerTouched, GripTouched;
    public InputDevice Device;
    public ControllerHardwareProfile Profile;
    public float Grip, Trigger, GripForce, TrackpadScroll;
    public Vector3 Position, AimPosition;
    public Quaternion Rotation, AimRotation;
    public Vector2 Axis, TrackpadAxis;
    // Per-finger curl from the runtime's hand tracking. Visual only.
    public FingerCurls Fingers;
}

internal static class TrackedControllers
{
    private static InputDevice? _reportedLeft, _reportedRight;
    private static readonly ControllerHardwareProfile[] ReportedProfiles = new ControllerHardwareProfile[2];
    private static int _availableFrame = -1;
    private static bool _available;
    private static readonly int[] ReadFrames = { -1, -1 };
    private static readonly ControllerSample[] Samples = new ControllerSample[2];
    private static Vector3 _sampleSeat, _sampleAnchor, _sampleTranslation;
    private static Quaternion _sampleRotation;
    // The Index interaction profile has trackpad force and touch, but no click.
    // A press is force past this threshold; it releases a little lower so a
    // steady thumb cannot chatter. ControllerBindings applies the saved value.
    internal const float DefaultTrackpadPressForce = .4f;
    internal static float TrackpadPressForce = DefaultTrackpadPressForce;
    private static readonly InputFeatureUsage<float> TrackpadForceUsage = new("Secondary2DAxisForce");
    private static readonly bool[] TrackpadHeld = new bool[2];
    // Grip force is separate from the grip value that holds a handle: the value
    // rises as the hand closes, force only when the handle is squeezed.
    internal const float DefaultGripSqueezeForce = .5f;
    internal static float GripSqueezeForce = DefaultGripSqueezeForce;
    private static readonly InputFeatureUsage<float> GripForceUsage = new("GripForce");
    private static readonly bool[] SqueezeHeld = new bool[2];
    internal static float TrackpadSwipeDistance = TrackpadSwipe.DefaultDistance;
    private static readonly TrackpadSwipe[] Swipes = { new(), new() };
    // Read at startup to decide whether the hand tracking extension is requested.
    internal static bool FingerTracking = true;
    private static readonly Vector3[] Joints = new Vector3[FingerCurlMath.JointCount];
    internal static bool ForcePressed(bool held, float force, float threshold) =>
        FlightControlMath.IsFinite(force) && FlightControlMath.IsFinite(threshold) && threshold > 0 &&
        force >= (held ? threshold * .75f : threshold);
    // One coherent runtime sample per hand per frame, shared by input, UI and visuals.
    // Recenter/seat edits invalidate mapped samples immediately.
    public static bool Available
    {
        get
        {
            if (_availableFrame != Time.frameCount)
            {
                _availableFrame = Time.frameCount;
                _available = CheckAvailable();
            }
            return _available;
        }
    }
    private static bool CheckAvailable()
    {
        // The desktop mirror can lose focus while the streamed headset still
        // owns the OpenXR session. The runtime, not the window, owns VR input.
        if (!XRSettings.enabled || !OpenXRUtility.IsSessionFocused)
            return false;
        var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        // Some streaming runtimes expose stale userPresence=false at startup.
        // Presence is diagnostic only; focused, tracked input must still work.
        return head.isValid && head.TryGetFeatureValue(CommonUsages.isTracked, out var tracked) && tracked;
    }

    internal static ControllerHardwareProfile Profile(bool left) => Profile(left ? XRController.leftHand : XRController.rightHand);
    private static ControllerHardwareProfile Profile(XRController? controller) => controller switch
    {
        Valve.OpenXR.Utils.SteamFrameControllerProfile.SteamFrameController => ControllerHardwareProfile.SteamFrame,
        ValveIndexControllerProfile.ValveIndexController => ControllerHardwareProfile.Index,
        OculusTouchControllerProfile.OculusTouchController => ControllerHardwareProfile.Touch,
        MetaQuestTouchPlusControllerProfile.QuestTouchPlusController => ControllerHardwareProfile.Touch,
        MetaQuestTouchProControllerProfile.QuestProTouchController => ControllerHardwareProfile.Touch,
        null => ControllerHardwareProfile.Unknown,
        _ => ControllerHardwareProfile.Other
    };
    internal static bool NativeFrame(bool left) => Profile(left) == ControllerHardwareProfile.SteamFrame;
    // Touch's right system button and both Index system buttons belong to the
    // runtime: SteamVR opens its dashboard and Pimax its overlay with them.
    internal static bool IsApplicationMenuAvailable(ControllerHardwareProfile profile, bool left) =>
        profile != ControllerHardwareProfile.Index && (profile != ControllerHardwareProfile.Touch || left);

    // SteamVR's legacy XR feature bridge and OpenXR's named Input System
    // controls need not expose exactly the same set of usages. Prefer an
    // available legacy feature, then the registered profile's matching action.
    private static bool ReadButton(InputDevice device, InputFeatureUsage<bool> usage, XRController? controller, string name) =>
        device.TryGetFeatureValue(usage, out var value) ? value : controller?.TryGetChildControl<ButtonControl>(name)?.isPressed == true;
    private static bool ReadAnalog(InputDevice device, InputFeatureUsage<float> usage, XRController? controller, string name, out float value)
    {
        if (device.TryGetFeatureValue(usage, out value) && FlightControlMath.IsFinite(value))
            return true;
        var control = controller?.TryGetChildControl<AxisControl>(name);
        if (control != null)
        {
            value = control.ReadValue();
            return true;
        }
        // Preserve invalid samples as invalid instead of treating tracking/data
        // failures as a released trigger. A genuinely absent feature is zero.
        if (!FlightControlMath.IsFinite(value))
            return true;
        value = 0;
        return false;
    }

    public static ControllerSample Read(bool left)
    {
        var seat = CockpitProfiles.SeatOffset;
        if (_sampleSeat != seat || _sampleAnchor != NOVRHeadsetData.TranslationAnchor ||
            _sampleTranslation != NOVRHeadsetData.TranslationCalibrationOffset || _sampleRotation != NOVRHeadsetData.RotationCalibrationOffset)
        {
            ReadFrames[0] = ReadFrames[1] = -1;
            _sampleSeat = seat;
            _sampleAnchor = NOVRHeadsetData.TranslationAnchor;
            _sampleTranslation = NOVRHeadsetData.TranslationCalibrationOffset;
            _sampleRotation = NOVRHeadsetData.RotationCalibrationOffset;
        }
        var i = left ? 0 : 1;
        if (ReadFrames[i] == Time.frameCount)
            return Samples[i];
        ReadFrames[i] = Time.frameCount;
        return Samples[i] = ReadRuntime(left);
    }
    private static ControllerSample ReadRuntime(bool left)
    {
        var sample = ReadTracked(left);
        // A press held through tracking loss must be made again afterwards.
        if (!sample.Valid)
        {
            var hand = left ? 0 : 1;
            TrackpadHeld[hand] = SqueezeHeld[hand] = false;
            Swipes[hand].Reset();
        }
        return sample;
    }
    private static ControllerSample ReadTracked(bool left)
    {
        if (!Available)
            return default;
        var device = InputDevices.GetDeviceAtXRNode(left ? XRNode.LeftHand : XRNode.RightHand);
        if (!device.isValid || !device.TryGetFeatureValue(CommonUsages.isTracked, out var tracked) || !tracked ||
            !device.TryGetFeatureValue(CommonUsages.trackingState, out var tracking) ||
            (tracking & (InputTrackingState.Position | InputTrackingState.Rotation)) !=
            (InputTrackingState.Position | InputTrackingState.Rotation) ||
            !device.TryGetFeatureValue(CommonUsages.devicePosition, out var position) ||
            !device.TryGetFeatureValue(CommonUsages.deviceRotation, out var rotation) ||
            !Finite(position) || !Finite(rotation))
            return default;

        var inputDevice = left ? XRController.leftHand : XRController.rightHand;
        var profile = Profile(inputDevice);
        var nativeFrame = profile == ControllerHardwareProfile.SteamFrame;
        var hasGrip = ReadAnalog(device, CommonUsages.grip, inputDevice, "grip", out var grip);
        var gripButton = ReadButton(device, CommonUsages.gripButton, inputDevice, "gripPressed");
        if (!ReadAnalog(device, CommonUsages.trigger, inputDevice, "trigger", out var trigger))
            trigger = ReadButton(device, CommonUsages.triggerButton, inputDevice, "triggerPressed") ? 1f : 0f;
        var hasAxis = device.TryGetFeatureValue(CommonUsages.primary2DAxis, out var axis) &&
                      FlightControlMath.IsFinite(axis.x) && FlightControlMath.IsFinite(axis.y);
        if (!hasAxis && inputDevice?.TryGetChildControl<Vector2Control>("thumbstick") is Vector2Control thumbstick)
        {
            axis = thumbstick.ReadValue();
            hasAxis = FlightControlMath.IsFinite(axis.x) && FlightControlMath.IsFinite(axis.y);
        }
        var stickClick = ReadButton(device, CommonUsages.primary2DAxisClick, inputDevice, "thumbstickClicked");
        var primary = ReadButton(device, CommonUsages.primaryButton, inputDevice, "primaryButton");
        var secondary = ReadButton(device, CommonUsages.secondaryButton, inputDevice, "secondaryButton");
        // Oculus/Meta's right system button belongs to the runtime, even though
        // Unity's stock Touch layout aliases it as MenuButton. Never route it
        // into cockpit actions; the physical left Menu remains the app button.
        var menu = IsApplicationMenuAvailable(profile, left) && ReadButton(device, CommonUsages.menuButton, inputDevice, "menu");

        // OpenXR generates device-specific layouts derived from the registered
        // profile, so their layout names need not equal the base layout name.
        var handIndex = left ? 0 : 1;
        if ((left ? _reportedLeft : _reportedRight)?.Equals(device) != true || ReportedProfiles[handIndex] != profile)
        {
            if (left)
                _reportedLeft = device;
            else
                _reportedRight = device;
            ReportedProfiles[handIndex] = profile;
            Debug.Log($"[NOVR] {(left ? "Left" : "Right")} controller: {device.name}; layout {inputDevice?.layout ?? "unavailable"}; profile {profile}.");
        }
        bool Button(string name) => inputDevice?.TryGetChildControl<ButtonControl>(name)?.isPressed == true;
        grip = FlightControlMath.GripValue(hasGrip, grip, gripButton || nativeFrame && Button("gripPressed"));
        bool Touch(string name, out bool supported)
        {
            var control = inputDevice?.TryGetChildControl<ButtonControl>(name);
            supported = control != null;
            return control?.isPressed == true;
        }
        var stickTouch = Touch("thumbstickTouched", out var hasStickTouch);
        var triggerTouch = Touch("triggerTouched", out var hasTriggerTouch);
        var primaryTouch = Touch(nativeFrame ? "faceButtonBottomTouched" : "primaryTouched", out var hasButtonTouch);
        var secondaryTouch = Button(nativeFrame ? "faceButtonInsideTouched" : "secondaryTouched");
        var topTouch = nativeFrame && Button("faceButtonTopTouched");
        var outsideTouch = nativeFrame && Button("faceButtonOutsideTouched");
        var faceTouch = primaryTouch || secondaryTouch || topTouch || outsideTouch;
        var restTouch = Touch("thumbrestTouched", out var hasRestTouch);
        var trackpad = false;
        var trackpadTouch = false;
        var squeeze = false;
        var gripForce = 0f;
        var hasPad = false;
        var pad = Vector2.zero;
        var swipe = TrackpadSwipeDirection.None;
        var hasFingers = false;
        FingerCurls fingers = default;
        if (profile == ControllerHardwareProfile.Index)
        {
            trackpadTouch = Button("trackpadTouched");
            ReadAnalog(device, TrackpadForceUsage, inputDevice, "trackpadForce", out var trackpadForce);
            trackpad = ForcePressed(TrackpadHeld[handIndex], trackpadForce, TrackpadPressForce);
            ReadAnalog(device, GripForceUsage, inputDevice, "gripForce", out gripForce);
            squeeze = ForcePressed(SqueezeHeld[handIndex], gripForce, GripSqueezeForce);
            if (!FlightControlMath.IsFinite(gripForce))
                gripForce = 0;
            hasPad = device.TryGetFeatureValue(CommonUsages.secondary2DAxis, out pad) &&
                     FlightControlMath.IsFinite(pad.x) && FlightControlMath.IsFinite(pad.y);
            if (!hasPad && inputDevice?.TryGetChildControl<Vector2Control>("trackpad") is Vector2Control padControl)
            {
                pad = padControl.ReadValue();
                hasPad = FlightControlMath.IsFinite(pad.x) && FlightControlMath.IsFinite(pad.y);
            }
            if (!hasPad)
                pad = Vector2.zero;
            swipe = Swipes[handIndex].Update(trackpadTouch && hasPad, pad.x, pad.y, trackpad, Time.unscaledTime, TrackpadSwipeDistance);
            // The runtime derives finger joints from the controller's own
            // sensors. They only shape the glove, never an input.
            hasFingers = FingerTracking && NovrHandJointsFeature.TryGetJointPositions(left, Joints) &&
                         FingerCurlMath.TryCurls(Joints, out fingers);
        }
        else
            Swipes[handIndex].Reset();
        TrackpadHeld[handIndex] = trackpad;
        SqueezeHeld[handIndex] = squeeze;
        var aimPosition = position;
        var aimRotation = rotation;
        var hasAimPose = false;
        // Use the runtime's aim pose, which differs from the grip pose on Frame.
        var pointer = inputDevice?.TryGetChildControl<UnityEngine.XR.OpenXR.Input.PoseControl>("pointer");
        if (pointer != null && pointer.isTracked.isPressed && (pointer.trackingState.ReadValue() & 3) == 3 &&
            Finite(pointer.position.ReadValue()) && Finite(pointer.rotation.ReadValue()))
        {
            aimPosition = pointer.position.ReadValue();
            aimRotation = pointer.rotation.ReadValue();
            hasAimPose = true;
        }
        return new ControllerSample
        {
            Valid = FlightControlMath.IsFinite(grip) && FlightControlMath.IsFinite(trigger),
            Device = device,
            Grip = grip,
            Trigger = trigger,
            HasAxis = hasAxis,
            HasAimPose = hasAimPose,
            Axis = hasAxis ? axis : Vector2.zero,
            StickClick = nativeFrame ? Button("thumbstickClicked") : stickClick,
            Primary = nativeFrame ? Button("faceButtonBottom") : primary,
            Secondary = nativeFrame ? Button("faceButtonInside") : secondary,
            Menu = nativeFrame ? Button("menu") : menu,
            Shoulder = Button("shoulder"),
            Top = Button("faceButtonTop"),
            Outside = Button("faceButtonOutside"),
            Trackpad = trackpad,
            GripSqueeze = squeeze,
            GripForce = gripForce,
            SwipeUp = swipe == TrackpadSwipeDirection.Up,
            SwipeDown = swipe == TrackpadSwipeDirection.Down,
            HasTrackpadAxis = hasPad && trackpadTouch,
            TrackpadAxis = hasPad && trackpadTouch ? pad : Vector2.zero,
            TrackpadScroll = profile == ControllerHardwareProfile.Index ? Swipes[handIndex].Scroll : 0,
            HasFingers = hasFingers,
            Fingers = fingers,
            NativeFrame = nativeFrame,
            Profile = profile,
            HasThumbTouch = hasStickTouch || hasButtonTouch || hasRestTouch,
            HasTriggerTouch = hasTriggerTouch,
            ThumbstickTouched = stickTouch,
            ButtonTouched = faceTouch,
            PrimaryTouched = primaryTouch,
            SecondaryTouched = secondaryTouch,
            TopTouched = topTouch,
            OutsideTouched = outsideTouch,
            // The Index thumb rests on its trackpad; there is no separate thumbrest.
            ThumbrestTouched = restTouch || trackpadTouch,
            TrackpadTouched = trackpadTouch,
            TriggerTouched = triggerTouch,
            GripTouched = Button("gripTouched"),
            Position = MapPosition(position),
            Rotation = NOVRHeadsetData.RotationCalibrationOffset * rotation,
            AimPosition = MapPosition(aimPosition),
            AimRotation = NOVRHeadsetData.RotationCalibrationOffset * aimRotation
        };
    }

    public static Vector3 MapPosition(Vector3 position) =>
        NOVRHeadsetData.MapTrackingPosition(position) + CockpitProfiles.SeatOffset;

    public static bool Finite(Vector3 value) => FlightControlMath.IsFinite(value.x) &&
        FlightControlMath.IsFinite(value.y) && FlightControlMath.IsFinite(value.z);
    private static bool Finite(Quaternion value) => Finite(new Vector3(value.x, value.y, value.z)) &&
        FlightControlMath.IsFinite(value.w) && Quaternion.Dot(value, value) > 0.5f;
}
