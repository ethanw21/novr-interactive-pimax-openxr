using System;

namespace NOVR.Compatibility;

internal enum HeadsetImageOpacity
{
    Automatic,
    Always,
    Off
}

// The bundled OpenXR plugin always submits the scene as a source-alpha,
// unpremultiplied projection layer. The game never authors that alpha channel.
// SteamVR treats the first layer as opaque, so it stays invisible there. A
// compositor that honors the flags (Pimax) multiplies the image by its alpha
// and shows its own grey backdrop instead of the game.
internal static class HeadsetImagePolicy
{
    // XrCompositionLayerFlags without BLEND_TEXTURE_SOURCE_ALPHA or UNPREMULTIPLIED_ALPHA.
    internal const ulong OpaqueLayerFlags = 0;

    // An unreadable saved value must not disable the compatibility default.
    internal static HeadsetImageOpacity Normalize(int saved) =>
        saved is >= (int)HeadsetImageOpacity.Automatic and <= (int)HeadsetImageOpacity.Off
            ? (HeadsetImageOpacity)saved
            : HeadsetImageOpacity.Automatic;

    // Costs nothing per frame and matches how every runtime already shows this game.
    internal static bool SubmitsOpaqueLayer(HeadsetImageOpacity mode) => mode != HeadsetImageOpacity.Off;

    // One full-screen blend per eye. Automatic limits that cost to compositors
    // known to read the first layer's alpha whatever its flags say.
    internal static bool WritesOpaqueAlpha(HeadsetImageOpacity mode, string? runtimeName) => mode switch
    {
        HeadsetImageOpacity.Always => true,
        HeadsetImageOpacity.Automatic => ReadsSceneAlpha(runtimeName),
        _ => false
    };

    internal static bool ReadsSceneAlpha(string? runtimeName) =>
        runtimeName != null && runtimeName.IndexOf("pimax", StringComparison.OrdinalIgnoreCase) >= 0;
}
