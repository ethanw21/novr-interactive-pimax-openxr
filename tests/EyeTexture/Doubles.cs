// Unity, the XR settings and the plugin's report are boundaries. The request
// handling, the policy and the component run unchanged. The OpenXR runtime is
// a stand-in reached through real function pointers, as the plugin reaches it.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace UnityEngine
{
    public class MonoBehaviour;
    public static class Debug
    {
        public static readonly List<string> Logs = new(), Warnings = new();
        public static void Log(object message) => Logs.Add(message.ToString()!);
        public static void LogWarning(object message) => Warnings.Add(message.ToString()!);
    }
    public static class Time
    {
        public static float unscaledTime;
    }
}
namespace UnityEngine.XR
{
    public static class XRSettings
    {
        public static bool enabled;
        public static int eyeTextureWidth, eyeTextureHeight;
    }
}
namespace UnityEngine.XR.OpenXR
{
    public static class NovrOpenXRReport
    {
        public static readonly List<string> Reasons = new();
        public static void Dump(string reason) => Reasons.Add(reason);
    }
}
namespace NOVR
{
    internal sealed class ConfigEntry<T>
    {
        internal T Value = default!;
    }
    internal sealed class ModConfiguration
    {
        internal static readonly ModConfiguration Instance = new();
        internal readonly ConfigEntry<Compatibility.EyeTextureRetry> EyeTextureRetry = new();
    }
}
namespace EyeTextureChecks
{
    // What the runtime was asked, read from native memory at the offsets the
    // OpenXR headers define for XrSwapchainCreateInfo on a 64-bit platform.
    internal readonly record struct Asked(IntPtr Address, int Type, IntPtr Next, ulong Flags, ulong Usage, long Format,
        uint Samples, uint Width, uint Height, uint Faces, uint Layers, uint Mips)
    {
        internal static Asked Read(IntPtr info) => new(info, Marshal.ReadInt32(info), Marshal.ReadIntPtr(info, 8),
            (ulong)Marshal.ReadInt64(info, 16), (ulong)Marshal.ReadInt64(info, 24), Marshal.ReadInt64(info, 32),
            (uint)Marshal.ReadInt32(info, 40), (uint)Marshal.ReadInt32(info, 44), (uint)Marshal.ReadInt32(info, 48),
            (uint)Marshal.ReadInt32(info, 52), (uint)Marshal.ReadInt32(info, 56), (uint)Marshal.ReadInt32(info, 60));
    }

    // The entry points are plain native-callable functions, like a real
    // runtime's: the code under test must wrap them itself, and is reached the
    // same way. They serve whichever stand-in was started last.
    internal sealed unsafe class Runtime
    {
        internal const ulong Instance = 0x1001, Session = 1;
        internal static Runtime Current = null!;
        internal static readonly IntPtr LookupPointer = (IntPtr)(delegate* unmanaged[Cdecl]<ulong, IntPtr, IntPtr, int>)&Lookup;
        internal static readonly IntPtr CreatePointer = (IntPtr)(delegate* unmanaged[Cdecl]<ulong, IntPtr, IntPtr, int>)&Create;
        private static readonly IntPtr DestroyPointer = (IntPtr)(delegate* unmanaged[Cdecl]<ulong, int>)&Destroy;
        private static readonly IntPtr FormatsPointer = (IntPtr)(delegate* unmanaged[Cdecl]<ulong, uint, IntPtr, IntPtr, int>)&EnumerateFormats;
        private static readonly IntPtr PropertiesPointer = (IntPtr)(delegate* unmanaged[Cdecl]<ulong, IntPtr, int>)&Properties;

        internal string Name = "Pimax OpenXR";
        internal long[] Formats = { 29, 91, 28, 87, 10 };
        internal Func<Asked, int> Answer = _ => 0;
        internal bool ProvidesDestroy = true, ProvidesFormats = true, ProvidesProperties = true;
        // Stands for another managed hook further down the chain.
        internal IntPtr CreateOverride;
        internal readonly List<Asked> Requests = new();
        internal readonly List<ulong> Created = new(), Destroyed = new();
        internal readonly List<string> Lookups = new();
        private ulong _nextHandle = 0x5000;

        internal IEnumerable<ulong> Alive => Created.Except(Destroyed);

        internal static int CallLookup(IntPtr lookup, ulong instance, IntPtr name, IntPtr function) =>
            ((delegate* unmanaged[Cdecl]<ulong, IntPtr, IntPtr, int>)lookup)(instance, name, function);

        internal static int CallCreate(IntPtr create, ulong session, IntPtr createInfo, IntPtr swapchain) =>
            ((delegate* unmanaged[Cdecl]<ulong, IntPtr, IntPtr, int>)create)(session, createInfo, swapchain);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static int Lookup(ulong instance, IntPtr name, IntPtr function)
        {
            var runtime = Current;
            var text = Marshal.PtrToStringAnsi(name)!;
            runtime.Lookups.Add(text);
            var pointer = text switch
            {
                "xrCreateInstance" when instance == 0 => new IntPtr(0x7001),
                "xrCreateSwapchain" when instance == Instance => runtime.CreateOverride != IntPtr.Zero ? runtime.CreateOverride : CreatePointer,
                "xrDestroySwapchain" when instance == Instance && runtime.ProvidesDestroy => DestroyPointer,
                "xrEnumerateSwapchainFormats" when instance == Instance && runtime.ProvidesFormats => FormatsPointer,
                "xrGetInstanceProperties" when instance == Instance && runtime.ProvidesProperties => PropertiesPointer,
                "xrEndFrame" when instance == Instance => new IntPtr(0x7002),
                _ => IntPtr.Zero
            };
            Marshal.WriteIntPtr(function, pointer);
            return pointer == IntPtr.Zero ? -7 : 0;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static int Create(ulong session, IntPtr createInfo, IntPtr swapchain)
        {
            var runtime = Current;
            if (session != Session)
                return -12;
            var asked = Asked.Read(createInfo);
            runtime.Requests.Add(asked);
            var result = runtime.Answer(asked);
            if (result < 0)
            {
                // A careless runtime may scribble on the output of a failed call.
                Marshal.WriteInt64(swapchain, unchecked((long)0xDEADDEADDEADDEAD));
                return result;
            }
            runtime.Created.Add(runtime._nextHandle);
            Marshal.WriteInt64(swapchain, (long)runtime._nextHandle++);
            return result;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static int Destroy(ulong swapchain)
        {
            Current.Destroyed.Add(swapchain);
            return Current.Created.Contains(swapchain) ? 0 : -12;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static int EnumerateFormats(ulong session, uint capacity, IntPtr count, IntPtr formats)
        {
            var known = Current.Formats;
            Marshal.WriteInt32(count, known.Length);
            if (capacity == 0)
                return 0;
            if (capacity < known.Length)
                return -11;
            Marshal.Copy(known, 0, formats, known.Length);
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static int Properties(ulong instance, IntPtr properties)
        {
            if (Marshal.ReadInt32(properties) != 32)
                return -1;
            var bytes = System.Text.Encoding.ASCII.GetBytes(Current.Name);
            Marshal.Copy(bytes, 0, properties + 24, Math.Min(bytes.Length, 127));
            return 0;
        }
    }
}
