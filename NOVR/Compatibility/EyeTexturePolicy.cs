using System;

namespace NOVR.Compatibility;

internal enum EyeTextureRetry
{
    Automatic,
    Always,
    Off
}

// Unity's OpenXR plugin asks the runtime for one colour texture per eye. A
// runtime that refuses leaves the headset on the compositor's grey backdrop:
// the plugin logs a result code and asks again every frame. NovrEyeTextureRequests
// then retries in forms the plugin can use unchanged and logs what the runtime
// accepts. This decides where that runs and when the wider report is written.
internal static class EyeTexturePolicy
{
    // Long enough for a slow runtime to hand over its first textures.
    internal const float ReportAfterSeconds = 5;

    // An unreadable saved value must not disable the compatibility default.
    internal static EyeTextureRetry Normalize(int saved) =>
        saved is >= (int)EyeTextureRetry.Automatic and <= (int)EyeTextureRetry.Off
            ? (EyeTextureRetry)saved
            : EyeTextureRetry.Automatic;

    internal static bool Watches(EyeTextureRetry mode) => mode != EyeTextureRetry.Off;

    // Automatic leaves the choice of runtime to NovrEyeTextureRequests.
    internal static bool WatchesEveryRuntime(EyeTextureRetry mode) => mode == EyeTextureRetry.Always;

    // VR is running, yet the runtime has given the plugin nothing to draw into.
    internal static bool ShouldReport(bool xr, int width, int height, float secondsInVr, bool reported) =>
        xr && !reported && (width <= 0 || height <= 0) && secondsInVr >= ReportAfterSeconds;
}
