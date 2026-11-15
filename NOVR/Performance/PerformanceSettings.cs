using BepInEx.Configuration;

namespace NOVR.Performance;

internal sealed class PerformanceSettings
{
    internal readonly ConfigEntry<bool> HoldOffFrameLimit;
    internal readonly ConfigEntry<bool> LogFrameTiming;
    internal readonly ConfigEntry<bool> ShareShadows;

    internal PerformanceSettings(ConfigFile config)
    {
        HoldOffFrameLimit = config.Bind("VR Performance", "Hold Off Game Frame Limit", true,
            "While a headset is active, hold off the game's own frame-rate limit and vertical sync so only the headset paces frames. The game's values are remembered and handed back when the headset stops or this is turned off.");
        LogFrameTiming = config.Bind("VR Performance", "Log Frame Timing", false,
            "Write a frame timing summary to Player.log every ten seconds while VR is active: frame rate, slowest frames, CPU main thread, render thread and GPU times, and memory churn. For comparing settings and builds. Disable after testing.");
        ShareShadows = config.Bind("VR Performance", "Share Shadows Between Eyes", false,
            "Experimental. Draw the sun's shadow map once per frame and use it for both eyes, where the game otherwise draws it once per eye. Shadows become identical in both eyes. Turn off if shadows flicker, vanish at the edge of view or differ between eyes.");
    }
}
