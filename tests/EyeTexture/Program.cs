using System.Reflection;
using System.Runtime.InteropServices;
using EyeTextureChecks;
using NOVR;
using NOVR.Compatibility;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR;
using Runtime = EyeTextureChecks.Runtime;

var checks = 0;
void Check(bool condition, string reason)
{
    checks++;
    if (!condition)
        throw new Exception(reason);
}

// The plugin's side of the boundary: names and requests in native memory,
// functions called through the pointers it was handed.
var logs = new List<string>();
(int Result, IntPtr Pointer) Resolve(IntPtr lookup, ulong instance, string name)
{
    var text = Marshal.StringToHGlobalAnsi(name);
    var slot = Marshal.AllocHGlobal(IntPtr.Size);
    Marshal.WriteIntPtr(slot, new IntPtr(-1));
    var result = Runtime.CallLookup(lookup, instance, text, slot);
    var pointer = Marshal.ReadIntPtr(slot);
    Marshal.FreeHGlobal(slot);
    Marshal.FreeHGlobal(text);
    return (result, pointer);
}
IntPtr Request(uint width = 4312, uint height = 5104, long format = 29, ulong usage = 0x21, ulong flags = 0,
    IntPtr next = default, uint layers = 1)
{
    var info = Marshal.AllocHGlobal(64);
    Marshal.WriteInt64(info, 0);
    Marshal.WriteInt32(info, 9);
    Marshal.WriteIntPtr(info, 8, next);
    Marshal.WriteInt64(info, 16, (long)flags);
    Marshal.WriteInt64(info, 24, (long)usage);
    Marshal.WriteInt64(info, 32, format);
    Marshal.WriteInt32(info, 40, 1);
    Marshal.WriteInt32(info, 44, (int)width);
    Marshal.WriteInt32(info, 48, (int)height);
    Marshal.WriteInt32(info, 52, 1);
    Marshal.WriteInt32(info, 56, (int)layers);
    Marshal.WriteInt32(info, 60, 1);
    return info;
}
(int Result, ulong Handle) Ask(IntPtr create, IntPtr info, ulong session = Runtime.Session)
{
    var handle = Marshal.AllocHGlobal(sizeof(long));
    Marshal.WriteInt64(handle, 0x1111);
    var result = Runtime.CallCreate(create, session, info, handle);
    var value = (ulong)Marshal.ReadInt64(handle);
    Marshal.FreeHGlobal(handle);
    return (result, value);
}
IntPtr Start(Runtime runtime, bool enabled = true, bool everyRuntime = false, Action<string>? log = null)
{
    logs.Clear();
    Runtime.Current = runtime;
    NovrEyeTextureRequests.Enabled = enabled;
    NovrEyeTextureRequests.EveryRuntime = everyRuntime;
    return NovrEyeTextureRequests.Hook(Runtime.LookupPointer, log ?? logs.Add);
}
IntPtr Watched(Runtime runtime, bool everyRuntime = false, Action<string>? log = null)
{
    var (result, create) = Resolve(Start(runtime, true, everyRuntime, log), Runtime.Instance, "xrCreateSwapchain");
    Check(result == 0 && create != IntPtr.Zero && create != Runtime.CreatePointer, "The request path is watched on " + runtime.Name);
    return create;
}
const string Standard = "4312x5104 format 29, usage 0x21, flags 0x0, samples 1, layers 1, mips 1, faces 1, extension data none";
const int Unsupported = -8;

// Where it runs.
foreach (var name in new[] { "PimaxXR (Unofficial)", "Pimax OpenXR", "pimax", "PIMAX Play" })
    Check(NovrEyeTextureRequests.RefusesStandardRequests(name), "Automatic watches " + name);
foreach (var other in new[] { "SteamVR/OpenXR", "Oculus", "Windows Mixed Reality Runtime", "VirtualDesktopXR", "OpenXR", "", null })
    Check(!NovrEyeTextureRequests.RefusesStandardRequests(other), "Automatic stays out of " + (other ?? "an unnamed runtime"));

var off = new Runtime();
Check(Start(off, enabled: false) == Runtime.LookupPointer && off.Lookups.Count == 0, "Off hands the plugin's own lookup straight back");
Check(NovrEyeTextureRequests.Status == "Off.", "Off is reported as such");
Check(NovrEyeTextureRequests.Hook(IntPtr.Zero, logs.Add) == IntPtr.Zero, "A missing lookup is not replaced");

foreach (var name in new[] { "SteamVR/OpenXR", "Oculus" })
{
    var other = new Runtime { Name = name };
    var lookup = Start(other);
    Check(lookup != Runtime.LookupPointer, "The lookup is wrapped before the runtime's name can be known");
    Check(Resolve(lookup, Runtime.Instance, "xrCreateSwapchain") == (0, Runtime.CreatePointer), name + " keeps its own xrCreateSwapchain");
    Check(logs.Count == 0 && NovrEyeTextureRequests.Status == "Not needed on " + name + ".", "Nothing is logged on " + name);
    Check(!other.Lookups.Contains("xrDestroySwapchain"), "No further functions are resolved on " + name);
    var always = Watched(other, everyRuntime: true);
    Check(logs.SequenceEqual(new[] { "[NOVR] Eye texture requests: watching on " + name + "." }), "Always watches " + name);
    Check(Ask(always, Request()) == (0, 0x5000UL), "A watched request still reaches " + name);
}
var unnamed = new Runtime { ProvidesProperties = false };
Check(Resolve(Start(unnamed), Runtime.Instance, "xrCreateSwapchain") == (0, Runtime.CreatePointer) &&
      NovrEyeTextureRequests.Status == "Not needed on OpenXR.", "A runtime that cannot be named is left alone");

// Every other lookup is the runtime's answer, untouched.
var accepting = new Runtime();
var acceptingLookup = Start(accepting);
Check(Resolve(acceptingLookup, 0, "xrCreateInstance") == (0, new IntPtr(0x7001)), "Functions resolved before the instance exists pass through");
Check(Resolve(acceptingLookup, Runtime.Instance, "xrEndFrame") == (0, new IntPtr(0x7002)), "Other functions pass through");
Check(Resolve(acceptingLookup, Runtime.Instance, "xrNoSuchFunction") == (-7, IntPtr.Zero), "A failed lookup keeps the runtime's result");
Check(Resolve(acceptingLookup, 0, "xrCreateSwapchain") == (-7, IntPtr.Zero), "A lookup the runtime refused is never replaced");
Check(logs.Count == 0 && accepting.Lookups.SequenceEqual(new[] { "xrCreateInstance", "xrEndFrame", "xrNoSuchFunction", "xrCreateSwapchain" }),
    "Nothing else is asked of the runtime until xrCreateSwapchain is resolved");
var (acceptingResult, acceptingCreate) = Resolve(acceptingLookup, Runtime.Instance, "xrCreateSwapchain");
Check(acceptingResult == 0 && acceptingCreate != Runtime.CreatePointer, "Pimax OpenXR is watched automatically");
Check(accepting.Lookups.Skip(4).SequenceEqual(new[] { "xrCreateSwapchain", "xrGetInstanceProperties", "xrDestroySwapchain", "xrEnumerateSwapchainFormats" }),
    "The runtime is named and the probe's functions resolved once");
Check(logs.SequenceEqual(new[] { "[NOVR] Eye texture requests: watching on Pimax OpenXR." }), "One line records the watch");

// An accepted request is the plugin's own, byte for byte, sent once.
var first = Request();
Check(Ask(acceptingCreate, first) == (0, 0x5000UL), "The runtime's swapchain reaches the plugin");
Check(accepting.Requests.Count == 1 && accepting.Requests[0].Address == first, "The plugin's own structure is passed through, not a copy");
Check(logs.Count == 2 && logs[1] == "[NOVR] Eye texture request accepted: " + Standard + ".", "The first accepted request is recorded in full: " + logs[^1]);
Check(Ask(acceptingCreate, Request()) == (0, 0x5001UL) && Ask(acceptingCreate, Request(usage: 0x22)) == (0, 0x5002UL), "Later requests pass through");
Check(logs.Count == 2 && accepting.Requests.Count == 3 && accepting.Destroyed.Count == 0, "Accepted requests cause no further work");
Check(NovrEyeTextureRequests.Status == "Accepted as requested: 4312x5104.", "Status names the accepted size");

// Optional parts the plugin can do without.
var extension = Marshal.AllocHGlobal(16);
Marshal.WriteInt64(extension, 0);
Marshal.WriteInt32(extension, 1000053005);
Marshal.WriteIntPtr(extension, 8, IntPtr.Zero);
var strict = new Runtime { Answer = asked => asked.Next != IntPtr.Zero || asked.Flags != 0 ? Unsupported : 0 };
var strictCreate = Watched(strict);
var chained = Request(next: extension, flags: 1);
Check(Ask(strictCreate, chained) == (0, 0x5000UL), "A request refused for its extension data is retried without it");
Check(strict.Requests.Count == 2 && strict.Requests[0].Address == chained && strict.Requests[0].Next == extension && strict.Requests[0].Flags == 1,
    "The plugin's request is tried first, unchanged");
var plain = strict.Requests[1];
Check(plain is { Type: 9, Flags: 0, Usage: 0x21, Format: 29, Samples: 1, Width: 4312, Height: 5104, Faces: 1, Layers: 1, Mips: 1 } &&
      plain.Next == IntPtr.Zero && plain.Address != chained, "The retry differs only in what the plugin does not need");
Check(logs.Skip(1).SequenceEqual(new[]
{
    "[NOVR] Eye texture request refused, XR_ERROR_FEATURE_UNSUPPORTED: 4312x5104 format 29, usage 0x21, flags 0x1, samples 1, layers 1, mips 1, faces 1, extension data 1000053005.",
    "[NOVR] Eye texture retry, " + Standard + ": XR_SUCCESS.",
    "[NOVR] Eye texture requests now use that form."
}), "The refusal and the accepted form are recorded: " + string.Join(" | ", logs));
Check(Ask(strictCreate, Request(next: extension, flags: 1)) == (0, 0x5001UL) && strict.Requests.Count == 4 && logs.Count == 4,
    "The other eye uses the accepted form without another report");
Check(Marshal.ReadInt32(chained, 16) == 1 && Marshal.ReadIntPtr(chained, 8) == extension, "The plugin's structure is never written to");
Check(strict.Destroyed.Count == 0 && NovrEyeTextureRequests.Status == "Accepted without extension data: 4312x5104.", "Nothing is created beyond what the plugin receives");

var noSampling = new Runtime { Answer = asked => (asked.Usage & 0x20) != 0 ? Unsupported : 0 };
var noSamplingCreate = Watched(noSampling);
Check(Ask(noSamplingCreate, Request()) == (0, 0x5000UL), "A request refused for its sampling usage is retried as a render target");
Check(noSampling.Requests.Count == 2 && noSampling.Requests[1] is { Usage: 1, Flags: 0, Format: 29, Width: 4312, Height: 5104, Layers: 1 },
    "A retry identical to the refused request is not sent");
Check(logs.Skip(2).SequenceEqual(new[]
{
    "[NOVR] Eye texture retry, 4312x5104 format 29, usage 0x1, flags 0x0, samples 1, layers 1, mips 1, faces 1, extension data none: XR_SUCCESS.",
    "[NOVR] Eye texture requests now use that form."
}) && NovrEyeTextureRequests.Status == "Accepted without the sampling request: 4312x5104.", "The reduced usage is recorded");

// A new start forgets what an earlier session learned.
Check(Ask(Watched(noSampling), Request()).Result == 0 && logs.Count == 4, "A restarted session reports and retries afresh");

// Refusals the plugin cannot work around are examined once and reported.
var sizeLimited = new Runtime { Answer = asked => asked.Width > 4000 || asked.Height > 4000 ? Unsupported : 0 };
var sizeCreate = Watched(sizeLimited);
Check(Ask(sizeCreate, Request()) == (Unsupported, 0UL), "The plugin receives the runtime's refusal and an empty handle");
Check(sizeLimited.Requests.Select(asked => (asked.Width, asked.Height, asked.Usage)).SequenceEqual(new (uint, uint, ulong)[]
{
    (4312, 5104, 0x21), (4312, 5104, 0x1), (1024, 1024, 0x21), (3880, 4592, 0x21), (3448, 4082, 0x21), (3018, 3572, 0x21)
}), "Sizes are tried downwards until one is accepted");
Check(sizeLimited.Created.Count == 2 && !sizeLimited.Alive.Any(), "Every probe swapchain is destroyed");
Check(logs.Skip(1).SequenceEqual(new[]
{
    "[NOVR] Eye texture request refused, XR_ERROR_FEATURE_UNSUPPORTED: " + Standard + ".",
    "[NOVR] Eye texture retry, 4312x5104 format 29, usage 0x1, flags 0x0, samples 1, layers 1, mips 1, faces 1, extension data none: XR_ERROR_FEATURE_UNSUPPORTED.",
    "[NOVR] Eye texture probe, runtime formats: 29, 91, 28, 87, 10.",
    "[NOVR] Eye texture probe, 1024x1024 format 29: XR_SUCCESS; 3880x4592: XR_ERROR_FEATURE_UNSUPPORTED; 3448x4082: XR_ERROR_FEATURE_UNSUPPORTED; 3018x3572: XR_SUCCESS."
}), "The log names the largest accepted size: " + string.Join(" | ", logs));
Check(NovrEyeTextureRequests.Status == "Refused, XR_ERROR_FEATURE_UNSUPPORTED: 4312x5104.", "Status names the refusal");
for (var frame = 0; frame < 3; frame++)
    Check(Ask(sizeCreate, Request()) == (Unsupported, 0UL), "The plugin's per-frame retry gets the same answer");
Check(sizeLimited.Requests.Count == 9 && logs.Count == 5 && sizeLimited.Created.Count == 2, "A refusal already examined costs one runtime call and no log lines");

var formatLimited = new Runtime { Answer = asked => asked.Format == 29 ? Unsupported : 0 };
var formatCreate = Watched(formatLimited);
Check(Ask(formatCreate, Request()) == (Unsupported, 0UL), "A refused format is not replaced behind the plugin's back");
Check(logs[^1] == "[NOVR] Eye texture probe, 1024x1024 format 29: XR_ERROR_FEATURE_UNSUPPORTED; " +
      "format 91: XR_SUCCESS, at 4312x5104: XR_SUCCESS; format 28: XR_SUCCESS, at 4312x5104: XR_SUCCESS; " +
      "format 87: XR_SUCCESS; format 10: XR_SUCCESS; 2 layer(s): XR_ERROR_FEATURE_UNSUPPORTED; usage 0x1: XR_ERROR_FEATURE_UNSUPPORTED.",
    "The log names the formats the runtime does create: " + logs[^1]);
Check(formatLimited.Created.Count == 6 && !formatLimited.Alive.Any(), "Every format probe is destroyed");
Check(formatLimited.Requests.Skip(2).All(asked => asked.Next == IntPtr.Zero && asked.Flags == 0 && asked.Samples == 1 && asked.Mips == 1 && asked.Faces == 1),
    "Probes vary one property at a time");

var refusing = new Runtime { Answer = _ => Unsupported, Formats = Array.Empty<long>() };
var refusingCreate = Watched(refusing);
Check(Ask(refusingCreate, Request()) == (Unsupported, 0UL) && refusing.Created.Count == 0 && refusing.Destroyed.Count == 0,
    "A runtime that creates nothing is asked to destroy nothing");
Check(logs.Contains("[NOVR] Eye texture probe, runtime formats: not reported."), "A missing format list is stated");
var afterFirst = refusing.Requests.Count;
Check(Ask(refusingCreate, Request(width: 2000, height: 2000)).Result == Unsupported && refusing.Requests.Count > afterFirst + 2,
    "A different refused request is examined too");
var afterSecond = refusing.Requests.Count;
var linesAfterSecond = logs.Count;
Check(Ask(refusingCreate, Request(width: 3000, height: 3000)) == (Unsupported, 0UL) && refusing.Requests.Count == afterSecond + 1 && logs.Count == linesAfterSecond,
    "Examinations are bounded for a plugin that keeps changing its request");

// Requests this is not for.
var depth = new Runtime { Answer = asked => (asked.Usage & 0x2) != 0 ? Unsupported : 0 };
var depthCreate = Watched(depth);
Check(Ask(depthCreate, Request(usage: 0x22)).Result == Unsupported && depth.Requests.Count == 1, "A refused depth texture is not retried");
Check(logs.Count == 2 && logs[1] == "[NOVR] Texture request refused, XR_ERROR_FEATURE_UNSUPPORTED: 4312x5104 format 29, usage 0x22, flags 0x0, samples 1, layers 1, mips 1, faces 1, extension data none.",
    "It is recorded once: " + logs[^1]);
Check(Ask(depthCreate, Request(usage: 0x22)).Result == Unsupported && logs.Count == 2 && depth.Requests.Count == 2, "Repeats stay quiet");
var lost = new Runtime();
var lostCreate = Watched(lost);
Check(Ask(lostCreate, Request(), session: 2).Result == -12 && lost.Requests.Count == 0 && logs.Count == 2 &&
      logs[1].StartsWith("[NOVR] Texture request refused, XR_ERROR_HANDLE_INVALID: "), "A dead session is not probed");

var noDestroy = new Runtime { ProvidesDestroy = false, Answer = sizeLimited.Answer };
var noDestroyCreate = Watched(noDestroy);
Check(Ask(noDestroyCreate, Request()) == (Unsupported, 0UL) && noDestroy.Requests.Count == 2 && noDestroy.Created.Count == 0,
    "Nothing is probed that could not be destroyed again");
Check(logs[^1] == "[NOVR] Eye texture probe skipped: the runtime did not provide xrDestroySwapchain.", "The skipped probe is stated");

// Nothing thrown here may reach native code.
var silent = new Runtime { Answer = strict.Answer };
var silentCreate = Watched(silent, log: _ => throw new InvalidOperationException("log sink failed"));
Check(Ask(silentCreate, Request(flags: 1)) == (0, 0x5000UL) && Ask(silentCreate, Request()) == (0, 0x5001UL), "A failing log sink changes no result");
var mute = new Runtime { Answer = sizeLimited.Answer };
logs.Clear();
Runtime.Current = mute;
NovrEyeTextureRequests.Enabled = true;
NovrEyeTextureRequests.EveryRuntime = false;
var (_, muteCreate) = Resolve(NovrEyeTextureRequests.Hook(Runtime.LookupPointer, null), Runtime.Instance, "xrCreateSwapchain");
Check(Ask(muteCreate, Request()) == (Unsupported, 0UL) && !mute.Alive.Any(), "No log sink is required");

// A pointer that cannot be wrapped leaves the plugin with what it had.
ForeignHook managed = () => 0;
var managedPointer = Marshal.GetFunctionPointerForDelegate(managed);
logs.Clear();
Check(NovrEyeTextureRequests.Hook(managedPointer, logs.Add) == managedPointer, "A lookup owned by another managed hook is handed back");
Check(logs.Count == 1 && logs[0].StartsWith("[NOVR] Eye texture requests: stopped starting after ") &&
      NovrEyeTextureRequests.Status == "Unavailable.", "The reason is recorded: " + string.Join(" | ", logs));
var foreign = new Runtime { CreateOverride = managedPointer };
Check(Resolve(Start(foreign), Runtime.Instance, "xrCreateSwapchain") == (0, managedPointer),
    "An xrCreateSwapchain owned by another managed hook stays in place");
Check(logs.Count == 1 && logs[0].StartsWith("[NOVR] Eye texture requests: stopped installing after "), "That is recorded as well: " + string.Join(" | ", logs));
GC.KeepAlive(managed);

// Policy and saved values.
Check(Enum.GetNames<EyeTextureRetry>().SequenceEqual(new[] { "Automatic", "Always", "Off" }) &&
      Enum.GetValues<EyeTextureRetry>().Select(value => (int)value).SequenceEqual(new[] { 0, 1, 2 }),
    "Saved modes keep their names and values");
Check(default(EyeTextureRetry) == EyeTextureRetry.Automatic, "A new configuration uses the compatible default");
foreach (var mode in Enum.GetValues<EyeTextureRetry>())
    Check(EyeTexturePolicy.Normalize((int)mode) == mode, "A valid saved mode survives: " + mode);
foreach (var invalid in new[] { -1, 3, 99, int.MinValue })
    Check(EyeTexturePolicy.Normalize(invalid) == EyeTextureRetry.Automatic, "An unreadable mode cannot disable the retry");
Check(EyeTexturePolicy.Watches(EyeTextureRetry.Automatic) && EyeTexturePolicy.Watches(EyeTextureRetry.Always) && !EyeTexturePolicy.Watches(EyeTextureRetry.Off),
    "Only Off leaves the plugin's request alone");
Check(!EyeTexturePolicy.WatchesEveryRuntime(EyeTextureRetry.Automatic) && EyeTexturePolicy.WatchesEveryRuntime(EyeTextureRetry.Always) &&
      !EyeTexturePolicy.WatchesEveryRuntime(EyeTextureRetry.Off), "Only Always watches every runtime");
Check(EyeTexturePolicy.ReportAfterSeconds == 5, "The report waits five seconds");
Check(EyeTexturePolicy.ShouldReport(true, 0, 0, 5, false) && EyeTexturePolicy.ShouldReport(true, 0, 3552, 60, false) && EyeTexturePolicy.ShouldReport(true, 3840, 0, 5, false),
    "VR without a complete eye texture size is reported");
Check(!EyeTexturePolicy.ShouldReport(false, 0, 0, 60, false) && !EyeTexturePolicy.ShouldReport(true, 0, 0, 4.9f, false) &&
      !EyeTexturePolicy.ShouldReport(true, 3840, 3552, 60, false) && !EyeTexturePolicy.ShouldReport(true, 0, 0, 60, true),
    "Flat play, a starting session, a working session and a repeat are not");

// The component.
static void Call(EyeTextureWatch target, string message) =>
    typeof(EyeTextureWatch).GetMethod(message, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);
var setting = ModConfiguration.Instance.EyeTextureRetry;
Exception? reportFailure = null;
EyeTextureWatch.WriteReport = reason =>
{
    if (reportFailure != null)
        throw reportFailure;
    NovrOpenXRReport.Dump(reason);
};
foreach (var (mode, enabled, every) in new[]
         {
             (EyeTextureRetry.Off, false, false), (EyeTextureRetry.Always, true, true), (EyeTextureRetry.Automatic, true, false),
             ((EyeTextureRetry)42, true, false)
         })
{
    NovrEyeTextureRequests.Enabled = !enabled;
    NovrEyeTextureRequests.EveryRuntime = !every;
    setting.Value = mode;
    Call(new EyeTextureWatch(), "OnEnable");
    Check(NovrEyeTextureRequests.Enabled == enabled && NovrEyeTextureRequests.EveryRuntime == every, "The saved mode is applied before OpenXR starts: " + mode);
}
setting.Value = EyeTextureRetry.Automatic;
Debug.Logs.Clear();
Debug.Warnings.Clear();
var watch = new EyeTextureWatch();
Call(watch, "OnEnable");
Time.unscaledTime = 100;
for (var frame = 0; frame < 3; frame++, Time.unscaledTime += 10)
    Call(watch, "Update");
Check(Debug.Warnings.Count == 0 && NovrOpenXRReport.Reasons.Count == 0, "Nothing is reported while flat");
setting.Value = EyeTextureRetry.Off;
Call(watch, "Update");
Check(!NovrEyeTextureRequests.Enabled, "A changed setting is passed on for the next start");
setting.Value = EyeTextureRetry.Automatic;
XRSettings.enabled = true;
Call(watch, "Update");
Time.unscaledTime += 4.9f;
Call(watch, "Update");
Check(Debug.Warnings.Count == 0 && NovrOpenXRReport.Reasons.Count == 0 && NovrEyeTextureRequests.Enabled, "A starting session is given time");
Time.unscaledTime += .2f;
Call(watch, "Update");
Check(NovrOpenXRReport.Reasons.SequenceEqual(new[] { "NOVR: no eye textures" }), "The plugin's report is written once eye textures are overdue");
Check(Debug.Warnings.Count == 1 && Debug.Warnings[0] == "[NOVR] No eye textures after 5 seconds in VR. A headset that is awake then shows only the runtime's backdrop. " +
      "Eye texture requests: " + NovrEyeTextureRequests.Status + " The OpenXR report follows.", "One warning introduces it: " + Debug.Warnings[0]);
for (var frame = 0; frame < 3; frame++, Time.unscaledTime += 10)
    Call(watch, "Update");
Check(Debug.Warnings.Count == 1 && NovrOpenXRReport.Reasons.Count == 1, "It is not repeated during the session");
XRSettings.enabled = false;
Call(watch, "Update");
XRSettings.enabled = true;
XRSettings.eyeTextureWidth = 3840;
XRSettings.eyeTextureHeight = 3552;
for (var frame = 0; frame < 3; frame++, Time.unscaledTime += 10)
    Call(watch, "Update");
Check(Debug.Warnings.Count == 1 && NovrOpenXRReport.Reasons.Count == 1, "A session with eye textures is never reported");
XRSettings.enabled = false;
Call(watch, "Update");
XRSettings.enabled = true;
XRSettings.eyeTextureWidth = XRSettings.eyeTextureHeight = 0;
reportFailure = new DllNotFoundException("UnityOpenXR");
Call(watch, "Update");
Time.unscaledTime += 6;
Call(watch, "Update");
Check(Debug.Warnings.Count == 3 && Debug.Warnings[2] == "[NOVR] The OpenXR report is unavailable: DllNotFoundException" && NovrOpenXRReport.Reasons.Count == 1,
    "A later session is reported again, and an unavailable report never interrupts the game");
Check(Debug.Logs.Count == 0, "The component itself logs nothing else");

Console.WriteLine($"Eye texture checks passed: {checks}");

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int ForeignHook();
