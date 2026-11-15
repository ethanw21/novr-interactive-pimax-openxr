namespace NOVR.Performance;

// What is known about the picture currently held in a renderer's sun shadow map.
internal readonly struct ShadowDraw
{
    internal readonly bool Reusable;
    internal readonly int Frame, Camera, Cascades, Width, Height;

    internal ShadowDraw(bool reusable, int frame, int camera, int cascades, int width, int height)
    {
        Reusable = reusable;
        Frame = frame;
        Camera = camera;
        Cascades = cascades;
        Height = height;
        Width = width;
    }
}

// With one render pass per eye the game draws the sun's shadow map twice per
// frame from viewpoints a few centimetres apart. The second eye may use the
// first eye's map instead, but only when that map is certainly the one this
// camera drew for its first eye in this very frame.
internal static class SharedShadowPolicy
{
    private static bool EyePass(bool xrEnabled, bool singlePass, int eye, int wanted) =>
        xrEnabled && !singlePass && eye == wanted;

    // Called for every real shadow map draw. Only a first-eye draw can be
    // shared; anything else that draws into the map makes it unusable.
    internal static ShadowDraw Record(bool xrEnabled, bool singlePass, int eye, int frame, int camera,
        int cascades, int width, int height) =>
        new(EyePass(xrEnabled, singlePass, eye, 0), frame, camera, cascades, width, height);

    // Known before culling: is this the second eye of the camera and frame
    // whose first eye drew the map that is still in place?
    internal static bool CanReuse(bool enabled, in ShadowDraw drawn, bool sameRenderer, bool textureAlive,
        bool xrEnabled, bool singlePass, int eye, int frame, int camera) =>
        enabled && drawn.Reusable && sameRenderer && textureAlive && EyePass(xrEnabled, singlePass, eye, 1) &&
        drawn.Frame == frame && drawn.Camera == camera;

    // Known once the eye's shadow settings are: they must be the first eye's.
    internal static bool SameShadowMap(in ShadowDraw drawn, bool mainLightCastsShadows, int cascades, int width, int height) =>
        mainLightCastsShadows && cascades > 0 && drawn.Cascades == cascades && drawn.Width == width && drawn.Height == height;

    // Finding shadow casters for the second eye is wasted work when its map
    // is not drawn, but other lights' shadow maps still need the result.
    internal static bool CanSkipCasterCulling(bool reuse, bool additionalLightShadows, float maxShadowDistance) =>
        reuse && !additionalLightShadows && maxShadowDistance > 0;
}
