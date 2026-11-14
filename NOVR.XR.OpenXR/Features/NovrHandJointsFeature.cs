using System;
using System.Runtime.InteropServices;

namespace UnityEngine.XR.OpenXR.Features;

// Reads XR_EXT_hand_tracking joint positions for NOVR's glove.
//
// Controllers that sense fingers, such as Valve Index, report them through this
// extension and not through their interaction profile. Unity's own support for
// it lives in the separate XR Hands package, which NOVR does not bundle, so the
// three functions are called directly. Everything here is optional: a runtime
// without the extension, or any failure, leaves the feature idle.
public sealed class NovrHandJointsFeature : OpenXRFeature
{
    public const string featureId = "com.novr.openxr.feature.handjoints";
    public const string handTrackingExtension = "XR_EXT_hand_tracking";
    // Joint queries need an OpenXR time. Unity does not expose its frame time
    // to features, so the current time is converted from the system counter.
    public const string timeExtension = "XR_KHR_win32_convert_performance_counter_time";
    public const string extensionStrings = handTrackingExtension + " " + timeExtension;
    public const int JointCount = 26;

    private const int TypeHandTrackerCreateInfo = 1000051001, TypeHandJointsLocateInfo = 1000051002, TypeHandJointLocations = 1000051003;
    private const ulong PositionValid = 2;
    // Stop asking after this many failures in a row; a new session tries again.
    private const int MaxFailures = 120;

    [StructLayout(LayoutKind.Sequential)]
    private struct CreateInfo
    {
        public int type;
        public IntPtr next;
        public int hand, handJointSet;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LocateInfo
    {
        public int type;
        public IntPtr next;
        public ulong baseSpace;
        public long time;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JointLocation
    {
        public ulong locationFlags;
        public float qx, qy, qz, qw, px, py, pz, radius;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Locations
    {
        public int type;
        public IntPtr next;
        public uint isActive, jointCount;
        public IntPtr jointLocations;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate int GetInstanceProcAddr(ulong instance, string name, out IntPtr function);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateHandTracker(ulong session, ref CreateInfo info, out ulong tracker);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DestroyHandTracker(ulong tracker);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int LocateHandJoints(ulong tracker, ref LocateInfo info, ref Locations locations);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ConvertCounterToTime(ulong instance, ref long counter, out long time);

    private static NovrHandJointsFeature? _active;
    private CreateHandTracker? _create;
    private DestroyHandTracker? _destroy;
    private LocateHandJoints? _locate;
    private ConvertCounterToTime? _convert;
    private ulong _instance, _session;
    private readonly ulong[] _trackers = new ulong[2];
    private readonly int[] _failures = new int[2];
    private readonly bool[] _reported = new bool[2], _failureReported = new bool[2];
    private readonly JointLocation[] _joints = new JointLocation[JointCount];

    protected internal override bool OnInstanceCreate(ulong xrInstance)
    {
        Forget();
        try
        {
            if (!OpenXRRuntime.IsExtensionEnabled(handTrackingExtension) || !OpenXRRuntime.IsExtensionEnabled(timeExtension))
            {
                Debug.Log("[NOVR] Finger tracking: this OpenXR runtime does not offer " + extensionStrings + ". Fingers follow grip and trigger.");
                return true;
            }
            var lookup = xrGetInstanceProcAddr;
            if (lookup == IntPtr.Zero)
                return true;
            var resolve = (GetInstanceProcAddr)Marshal.GetDelegateForFunctionPointer(lookup, typeof(GetInstanceProcAddr));
            _create = Resolve<CreateHandTracker>(resolve, xrInstance, "xrCreateHandTrackerEXT");
            _destroy = Resolve<DestroyHandTracker>(resolve, xrInstance, "xrDestroyHandTrackerEXT");
            _locate = Resolve<LocateHandJoints>(resolve, xrInstance, "xrLocateHandJointsEXT");
            _convert = Resolve<ConvertCounterToTime>(resolve, xrInstance, "xrConvertWin32PerformanceCounterToTimeKHR");
            if (_create == null || _destroy == null || _locate == null || _convert == null)
            {
                Debug.Log("[NOVR] Finger tracking: the OpenXR runtime did not provide its hand tracking functions.");
                Forget();
                return true;
            }
            _instance = xrInstance;
            _active = this;
            Debug.Log("[NOVR] Finger tracking: OpenXR hand tracking is available.");
        }
        catch (Exception e)
        {
            Debug.Log("[NOVR] Finger tracking is off: " + e.Message);
            Forget();
        }
        // Never fail the loader over an optional visual.
        return true;
    }

    private static T? Resolve<T>(GetInstanceProcAddr resolve, ulong instance, string name) where T : Delegate =>
        resolve(instance, name, out var function) >= 0 && function != IntPtr.Zero
            ? (T)Marshal.GetDelegateForFunctionPointer(function, typeof(T))
            : null;

    protected internal override void OnSessionCreate(ulong xrSession)
    {
        // Trackers belong to a session and are created on first use.
        Array.Clear(_trackers, 0, _trackers.Length);
        Array.Clear(_failures, 0, _failures.Length);
        _session = xrSession;
    }

    protected internal override void OnSessionDestroy(ulong xrSession)
    {
        for (var i = 0; i < _trackers.Length; i++)
        {
            if (_trackers[i] != 0 && _destroy != null)
            {
                try
                {
                    _destroy(_trackers[i]);
                }
                catch (Exception)
                {
                    // The session is going away with its trackers either way.
                }
            }
            _trackers[i] = 0;
        }
        _session = 0;
    }

    protected internal override void OnInstanceDestroy(ulong xrInstance) => Forget();

    private void Forget()
    {
        _create = null;
        _destroy = null;
        _locate = null;
        _convert = null;
        _instance = _session = 0;
        Array.Clear(_trackers, 0, _trackers.Length);
        Array.Clear(_failures, 0, _failures.Length);
        if (_active == this)
            _active = null;
    }

    // True while a session can be asked for joints at all.
    public static bool Available => _active != null && _active._session != 0;

    // Fills positions with the hand's joints in XR_EXT_hand_tracking order, in
    // OpenXR app-space coordinates. False when the runtime is not tracking that
    // hand's fingers right now; positions are then left as they were.
    public static bool TryGetJointPositions(bool left, Vector3[] positions)
    {
        var feature = _active;
        if (feature == null || positions == null || positions.Length < JointCount)
            return false;
        try
        {
            return feature.Locate(left ? 0 : 1, positions);
        }
        catch (Exception e)
        {
            Debug.Log("[NOVR] Finger tracking stopped: " + e.Message);
            feature.Forget();
            return false;
        }
    }

    private bool Locate(int hand, Vector3[] positions)
    {
        if (_session == 0 || _create == null || _locate == null || _convert == null || _failures[hand] >= MaxFailures)
            return false;
        if (_trackers[hand] == 0)
        {
            // XR_HAND_LEFT_EXT = 1, XR_HAND_RIGHT_EXT = 2, default joint set = 0.
            var create = new CreateInfo { type = TypeHandTrackerCreateInfo, hand = hand + 1 };
            var created = _create(_session, ref create, out var tracker);
            if (created < 0 || tracker == 0)
                return Failed(hand, "xrCreateHandTrackerEXT", created, true);
            _trackers[hand] = tracker;
        }
        var space = GetCurrentAppSpace();
        if (space == 0 || !QueryPerformanceCounter(out var counter))
            return false;
        var converted = _convert(_instance, ref counter, out var time);
        if (converted < 0 || time <= 0)
            return Failed(hand, "xrConvertWin32PerformanceCounterToTimeKHR", converted, false);
        var locate = new LocateInfo { type = TypeHandJointsLocateInfo, baseSpace = space, time = time };
        var locations = new Locations { type = TypeHandJointLocations, jointCount = JointCount };
        int located;
        unsafe
        {
            fixed (JointLocation* joints = _joints)
            {
                locations.jointLocations = (IntPtr)joints;
                located = _locate(_trackers[hand], ref locate, ref locations);
            }
        }
        if (located < 0)
            return Failed(hand, "xrLocateHandJointsEXT", located, false);
        _failures[hand] = 0;
        if (locations.isActive == 0)
            return false;
        // Joint 0 is the palm, which finger bends do not use.
        for (var i = 1; i < JointCount; i++)
            if ((_joints[i].locationFlags & PositionValid) == 0)
                return false;
        for (var i = 0; i < JointCount; i++)
            positions[i] = new Vector3(_joints[i].px, _joints[i].py, _joints[i].pz);
        if (!_reported[hand])
        {
            _reported[hand] = true;
            Debug.Log("[NOVR] Finger tracking: the runtime is reporting " + (hand == 0 ? "left" : "right") + " hand joints.");
        }
        return true;
    }

    private bool Failed(int hand, string function, int result, bool final)
    {
        if (!_failureReported[hand])
        {
            _failureReported[hand] = true;
            Debug.Log("[NOVR] Finger tracking: " + function + " failed for the " + (hand == 0 ? "left" : "right") + " hand (XrResult " + result + ").");
        }
        _failures[hand] = final ? MaxFailures : _failures[hand] + 1;
        return false;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryPerformanceCounter(out long counter);
}
