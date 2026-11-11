using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace UnityEngine.XR.OpenXR;

// When a runtime refuses the eye texture request, the bundled OpenXR plugin
// logs only the result code and asks again every frame, so the headset stays
// on the compositor's backdrop. This stands between the plugin and the runtime
// for xrCreateSwapchain alone. An accepted request passes through untouched. A
// refused colour request is reported in full, retried in forms the plugin can
// use unchanged (the same images, fewer optional requests), and otherwise
// examined once with throwaway requests so the log names what the runtime
// does accept. Nothing here changes a size, a format or a layer count: the
// plugin would not know, and would draw into images it did not ask for.
//
// No Unity types: the portable checks drive this file against a stand-in
// runtime through the same function pointers the plugin uses.
public static class NovrEyeTextureRequests
{
    // Set by the mod from its configuration. Read when OpenXR starts.
    public static bool Enabled = true;
    // False limits the hook to runtimes known to refuse the plugin's request.
    public static bool EveryRuntime;

    // Plain text for the mod's status display. Written on the render thread.
    public static string Status
    {
        get { lock (Gate) return _status; }
    }

    private const int StructureTypeSwapchainCreateInfo = 9;
    private const int StructureTypeInstanceProperties = 32;
    private const int InstancePropertiesSize = 152; // type, next, version, char[128]
    private const int RuntimeNameOffset = 24;
    private const ulong UsageColorAttachment = 0x1;
    private const ulong UsageDepthStencilAttachment = 0x2;
    private const int HandleInvalid = -12, InstanceLost = -13, SessionLost = -17;
    private const uint ProbeEdge = 1024;
    private const int MaximumExaminations = 2, MaximumFormats = 16, MaximumFullSizeFormats = 2, MaximumChain = 4;
    private static readonly float[] ProbeScales = { .9f, .8f, .7f, .6f, .5f, .4f, .3f };

    [StructLayout(LayoutKind.Sequential)]
    private struct CreateInfo
    {
        public int Type;
        public IntPtr Next;
        public ulong CreateFlags;
        public ulong UsageFlags;
        public long Format;
        public uint SampleCount, Width, Height, FaceCount, ArraySize, MipCount;
    }

    // Only blittable parameters, so entering from the render thread needs no marshalling.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int LookupFunction(ulong instance, IntPtr name, IntPtr function);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateFunction(ulong session, IntPtr createInfo, IntPtr swapchain);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DestroyFunction(ulong swapchain);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FormatsFunction(ulong session, uint capacity, IntPtr count, IntPtr formats);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PropertiesFunction(ulong instance, IntPtr properties);

    private enum Remedy
    {
        None,
        // Drops extension structures and creation flags.
        Plain,
        // Also asks for a render target only. Unity samples the eye texture
        // just for the desktop mirror, which may then stay black.
        RenderTargetOnly
    }

    private static readonly object Gate = new();
    // The runtime keeps these pointers for the life of the process.
    private static readonly LookupFunction LookupHook = Lookup;
    private static readonly CreateFunction CreateHook = Create;
    private static readonly IntPtr LookupHookPointer = Marshal.GetFunctionPointerForDelegate(LookupHook);
    private static readonly IntPtr CreateHookPointer = Marshal.GetFunctionPointerForDelegate(CreateHook);
    private static readonly HashSet<string> Examined = new();
    private static LookupFunction? _nextLookup;
    private static CreateFunction? _create;
    private static DestroyFunction? _destroy;
    private static FormatsFunction? _formats;
    private static Action<string>? _log;
    private static Remedy _remedy;
    private static int _examinations;
    private static bool _acceptedReported, _otherReported, _faultReported;
    private static string _status = "Not started.";

    // Called with the plugin's xrGetInstanceProcAddr before the instance
    // exists. Returns the pointer the plugin should use instead.
    public static IntPtr Hook(IntPtr lookup, Action<string>? log)
    {
        lock (Gate)
        {
            _log = log;
            _nextLookup = null;
            _create = null;
            _destroy = null;
            _formats = null;
            _remedy = Remedy.None;
            _examinations = 0;
            _acceptedReported = _otherReported = _faultReported = false;
            Examined.Clear();
            if (!Enabled || lookup == IntPtr.Zero || lookup == LookupHookPointer)
            {
                _status = Enabled ? "Not started." : "Off.";
                return lookup;
            }
            try { _nextLookup = Wrap<LookupFunction>(lookup); }
            catch (Exception error)
            {
                // Another managed hook's pointer cannot be wrapped as this delegate type.
                // OpenXR must still start, so the plugin keeps the lookup it had.
                _status = "Unavailable.";
                Fault("starting", error);
                return lookup;
            }
            _status = "Waiting for the VR runtime.";
            return LookupHookPointer;
        }
    }

    private static int Lookup(ulong instance, IntPtr name, IntPtr function)
    {
        var next = _nextLookup;
        if (next == null)
            return -7; // XR_ERROR_FUNCTION_UNSUPPORTED: Hook was never completed.
        var result = next(instance, name, function);
        try
        {
            if (result >= 0 && instance != 0 && name != IntPtr.Zero && function != IntPtr.Zero &&
                Marshal.PtrToStringAnsi(name) == "xrCreateSwapchain")
                Watch(next, instance, function);
        }
        catch (Exception error) { Fault("installing", error); }
        return result;
    }

    private static void Watch(LookupFunction next, ulong instance, IntPtr function)
    {
        var original = Marshal.ReadIntPtr(function);
        if (original == IntPtr.Zero || original == CreateHookPointer)
            return;
        var runtime = RuntimeName(next, instance);
        lock (Gate)
        {
            if (!EveryRuntime && !RefusesStandardRequests(runtime))
            {
                _status = "Not needed on " + runtime + ".";
                return;
            }
            _create = Wrap<CreateFunction>(original);
            var destroy = Resolve(next, instance, "xrDestroySwapchain");
            var formats = Resolve(next, instance, "xrEnumerateSwapchainFormats");
            _destroy = destroy == IntPtr.Zero ? null : Wrap<DestroyFunction>(destroy);
            _formats = formats == IntPtr.Zero ? null : Wrap<FormatsFunction>(formats);
            _status = "Watching requests on " + runtime + ".";
        }
        Marshal.WriteIntPtr(function, CreateHookPointer);
        Say("Eye texture requests: watching on " + runtime + ".");
    }

    // The long-established overloads, as the rest of the mod uses under Unity's Mono.
    private static T Wrap<T>(IntPtr function) where T : Delegate =>
        (T)Marshal.GetDelegateForFunctionPointer(function, typeof(T));

    // The automatic choice. Other runtimes accept the plugin's request as sent.
    public static bool RefusesStandardRequests(string? runtimeName) =>
        runtimeName != null && runtimeName.IndexOf("pimax", StringComparison.OrdinalIgnoreCase) >= 0;

    private static IntPtr Resolve(LookupFunction next, ulong instance, string name)
    {
        var text = Marshal.StringToHGlobalAnsi(name);
        var slot = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            Marshal.WriteIntPtr(slot, IntPtr.Zero);
            return next(instance, text, slot) >= 0 ? Marshal.ReadIntPtr(slot) : IntPtr.Zero;
        }
        finally
        {
            Marshal.FreeHGlobal(slot);
            Marshal.FreeHGlobal(text);
        }
    }

    private static string RuntimeName(LookupFunction next, ulong instance)
    {
        var pointer = Resolve(next, instance, "xrGetInstanceProperties");
        if (pointer == IntPtr.Zero)
            return "OpenXR";
        var properties = Marshal.AllocHGlobal(InstancePropertiesSize);
        try
        {
            for (var offset = 0; offset < InstancePropertiesSize; offset++)
                Marshal.WriteByte(properties, offset, 0);
            Marshal.WriteInt32(properties, StructureTypeInstanceProperties);
            if (Wrap<PropertiesFunction>(pointer)(instance, properties) < 0)
                return "OpenXR";
            Marshal.WriteByte(properties, InstancePropertiesSize - 1, 0);
            var name = Marshal.PtrToStringAnsi(properties + RuntimeNameOffset);
            return string.IsNullOrWhiteSpace(name) ? "OpenXR" : name!;
        }
        finally { Marshal.FreeHGlobal(properties); }
    }

    private static int Create(ulong session, IntPtr createInfo, IntPtr swapchain)
    {
        var create = _create;
        if (create == null)
            return -2; // XR_ERROR_RUNTIME_FAILURE: never installed without a target.
        // The plugin's own request always goes first, exactly as it was made.
        var result = create(session, createInfo, swapchain);
        try
        {
            if (createInfo == IntPtr.Zero || swapchain == IntPtr.Zero)
                return result;
            lock (Gate)
                return Review(create, session, createInfo, swapchain, result);
        }
        catch (Exception error)
        {
            Fault("reviewing", error);
            return result;
        }
    }

    private static int Review(CreateFunction create, ulong session, IntPtr createInfo, IntPtr swapchain, int result)
    {
        if (Marshal.PtrToStructure(createInfo, typeof(CreateInfo)) is not CreateInfo request ||
            request.Type != StructureTypeSwapchainCreateInfo)
            return result;
        var colour = (request.UsageFlags & UsageColorAttachment) != 0 &&
            (request.UsageFlags & UsageDepthStencilAttachment) == 0;
        if (result >= 0)
        {
            if (colour && !_acceptedReported)
            {
                _acceptedReported = true;
                _status = "Accepted as requested: " + Size(request) + ".";
                Say("Eye texture request accepted: " + Describe(request) + ".");
            }
            return result;
        }
        if (!colour || result is HandleInvalid or InstanceLost or SessionLost)
        {
            if (!_otherReported)
                Say("Texture request refused, " + ResultName(result) + ": " + Describe(request) + ".");
            _otherReported = true;
            return result;
        }
        // What already worked for the other eye.
        if (_remedy != Remedy.None && !Same(Apply(request, _remedy), request))
        {
            var repeated = Send(create, session, Apply(request, _remedy), swapchain);
            if (repeated >= 0)
                return repeated;
        }
        // The plugin asks again every frame. Each distinct request is examined once.
        if (_examinations >= MaximumExaminations || !Examined.Add(Describe(request) + result))
            return Refuse(swapchain, result);
        _examinations++;
        Say("Eye texture request refused, " + ResultName(result) + ": " + Describe(request) + ".");
        foreach (var remedy in new[] { Remedy.Plain, Remedy.RenderTargetOnly })
        {
            var attempt = Apply(request, remedy);
            if (Same(attempt, request))
                continue;
            var answer = Send(create, session, attempt, swapchain);
            Say("Eye texture retry, " + Describe(attempt) + ": " + ResultName(answer) + ".");
            if (answer < 0)
                continue;
            _remedy = remedy;
            _status = "Accepted without " + (remedy == Remedy.Plain ? "extension data" : "the sampling request") +
                ": " + Size(request) + ".";
            Say("Eye texture requests now use that form.");
            return answer;
        }
        _status = "Refused, " + ResultName(result) + ": " + Size(request) + ".";
        Examine(create, session, request);
        return Refuse(swapchain, result);
    }

    // A failed retry may have written to the plugin's handle.
    private static int Refuse(IntPtr swapchain, int result)
    {
        Marshal.WriteInt64(swapchain, 0);
        return result;
    }

    private static CreateInfo Apply(CreateInfo request, Remedy remedy)
    {
        request.Next = IntPtr.Zero;
        request.CreateFlags = 0;
        if (remedy == Remedy.RenderTargetOnly)
            request.UsageFlags = UsageColorAttachment;
        return request;
    }

    private static bool Same(CreateInfo a, CreateInfo b) =>
        a.Next == b.Next && a.CreateFlags == b.CreateFlags && a.UsageFlags == b.UsageFlags;

    private static int Send(CreateFunction create, ulong session, CreateInfo request, IntPtr swapchain)
    {
        var copy = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(CreateInfo)));
        try
        {
            Marshal.StructureToPtr((object)request, copy, false);
            return create(session, copy, swapchain);
        }
        finally { Marshal.FreeHGlobal(copy); }
    }

    // Throwaway requests. Each accepted one is destroyed at once; none reaches the plugin.
    private static void Examine(CreateFunction create, ulong session, CreateInfo request)
    {
        var destroy = _destroy;
        if (destroy == null)
        {
            Say("Eye texture probe skipped: the runtime did not provide xrDestroySwapchain.");
            return;
        }
        var handle = Marshal.AllocHGlobal(sizeof(long));
        try
        {
            int Probe(CreateInfo probe)
            {
                Marshal.WriteInt64(handle, 0);
                var answer = Send(create, session, probe, handle);
                var created = (ulong)Marshal.ReadInt64(handle);
                if (answer >= 0 && created != 0)
                    destroy(created);
                return answer;
            }

            var formats = Formats(session);
            Say("Eye texture probe, runtime formats: " + (formats.Count == 0 ? "not reported" : string.Join(", ", formats)) + ".");
            var small = Apply(request, Remedy.Plain);
            small.Width = Math.Min(request.Width, ProbeEdge);
            small.Height = Math.Min(request.Height, ProbeEdge);
            var smallAnswer = Probe(small);
            var report = new StringBuilder("Eye texture probe, ").Append(Size(small)).Append(" format ")
                .Append(small.Format).Append(": ").Append(ResultName(smallAnswer));
            if (smallAnswer >= 0)
            {
                // The format and options are fine. Find the largest size that is not refused.
                foreach (var scale in ProbeScales)
                {
                    var sized = Apply(request, Remedy.Plain);
                    sized.Width = Even(request.Width * scale);
                    sized.Height = Even(request.Height * scale);
                    if (sized.Width <= small.Width && sized.Height <= small.Height)
                        break;
                    var answer = Probe(sized);
                    report.Append("; ").Append(Size(sized)).Append(": ").Append(ResultName(answer));
                    if (answer >= 0)
                        break;
                }
                Say(report.Append('.').ToString());
                return;
            }
            // Not the size. See which of the runtime's own formats it will create at all.
            int tried = 0, fullSize = 0;
            foreach (var format in formats)
            {
                if (format == request.Format || tried++ >= MaximumFormats)
                    continue;
                var other = small;
                other.Format = format;
                var answer = Probe(other);
                report.Append("; format ").Append(format).Append(": ").Append(ResultName(answer));
                // Full-size images are large. Two accepted formats are enough to choose from.
                if (answer < 0 || fullSize++ >= MaximumFullSizeFormats)
                    continue;
                other.Width = request.Width;
                other.Height = request.Height;
                report.Append(", at ").Append(Size(other)).Append(": ").Append(ResultName(Probe(other)));
            }
            foreach (var layers in new uint[] { 1, 2 })
            {
                if (layers == request.ArraySize)
                    continue;
                var other = small;
                other.ArraySize = layers;
                report.Append("; ").Append(layers).Append(" layer(s): ").Append(ResultName(Probe(other)));
            }
            var target = small;
            target.UsageFlags = UsageColorAttachment;
            if (target.UsageFlags != small.UsageFlags)
                report.Append("; usage 0x1: ").Append(ResultName(Probe(target)));
            Say(report.Append('.').ToString());
        }
        finally { Marshal.FreeHGlobal(handle); }
    }

    private static List<long> Formats(ulong session)
    {
        var list = new List<long>();
        var formats = _formats;
        if (formats == null)
            return list;
        var count = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(count, 0);
            if (formats(session, 0, count, IntPtr.Zero) < 0)
                return list;
            var capacity = Math.Min(Marshal.ReadInt32(count), 64);
            if (capacity <= 0)
                return list;
            var values = Marshal.AllocHGlobal(capacity * sizeof(long));
            try
            {
                Marshal.WriteInt32(count, 0);
                if (formats(session, (uint)capacity, count, values) < 0)
                    return list;
                var written = Math.Min(Marshal.ReadInt32(count), capacity);
                for (var index = 0; index < written; index++)
                    list.Add(Marshal.ReadInt64(values, index * sizeof(long)));
            }
            finally { Marshal.FreeHGlobal(values); }
        }
        finally { Marshal.FreeHGlobal(count); }
        return list;
    }

    // The plugin only asks for even sizes.
    private static uint Even(float value) => Math.Max(2u, (uint)(value / 2) * 2);

    private static string Size(CreateInfo request) =>
        request.Width.ToString(CultureInfo.InvariantCulture) + "x" + request.Height.ToString(CultureInfo.InvariantCulture);

    private static string Describe(CreateInfo request)
    {
        var text = new StringBuilder(Size(request));
        text.Append(" format ").Append(request.Format.ToString(CultureInfo.InvariantCulture))
            .Append(", usage 0x").Append(request.UsageFlags.ToString("x", CultureInfo.InvariantCulture))
            .Append(", flags 0x").Append(request.CreateFlags.ToString("x", CultureInfo.InvariantCulture))
            .Append(", samples ").Append(request.SampleCount)
            .Append(", layers ").Append(request.ArraySize)
            .Append(", mips ").Append(request.MipCount)
            .Append(", faces ").Append(request.FaceCount)
            .Append(", extension data ");
        if (request.Next == IntPtr.Zero)
            return text.Append("none").ToString();
        // Every OpenXR structure starts with its type and the next pointer.
        var next = request.Next;
        for (var depth = 0; next != IntPtr.Zero && depth < MaximumChain; depth++)
        {
            text.Append(depth == 0 ? "" : "+").Append(Marshal.ReadInt32(next).ToString(CultureInfo.InvariantCulture));
            next = Marshal.ReadIntPtr(next, IntPtr.Size);
        }
        return text.ToString();
    }

    private static string ResultName(int result) => result switch
    {
        0 => "XR_SUCCESS",
        -1 => "XR_ERROR_VALIDATION_FAILURE",
        -2 => "XR_ERROR_RUNTIME_FAILURE",
        -3 => "XR_ERROR_OUT_OF_MEMORY",
        -7 => "XR_ERROR_FUNCTION_UNSUPPORTED",
        -8 => "XR_ERROR_FEATURE_UNSUPPORTED",
        -10 => "XR_ERROR_LIMIT_REACHED",
        -11 => "XR_ERROR_SIZE_INSUFFICIENT",
        HandleInvalid => "XR_ERROR_HANDLE_INVALID",
        InstanceLost => "XR_ERROR_INSTANCE_LOST",
        -16 => "XR_ERROR_SESSION_NOT_RUNNING",
        SessionLost => "XR_ERROR_SESSION_LOST",
        -26 => "XR_ERROR_SWAPCHAIN_FORMAT_UNSUPPORTED",
        _ => "XrResult " + result.ToString(CultureInfo.InvariantCulture)
    };

    private static void Say(string message)
    {
        try { _log?.Invoke("[NOVR] " + message); }
        catch (Exception) { /* Logging must never fail a runtime call. */ }
    }

    // An exception must not cross back into native code.
    private static void Fault(string stage, Exception error)
    {
        if (_faultReported)
            return;
        _faultReported = true;
        Say("Eye texture requests: stopped " + stage + " after " + error.GetType().Name + ": " + error.Message);
    }
}
