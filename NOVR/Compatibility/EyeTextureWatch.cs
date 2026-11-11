using System;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR;

namespace NOVR.Compatibility;

// Applies EyeTexturePolicy. The request handling itself lives beside the
// OpenXR plugin, in NovrEyeTextureRequests, and runs when the runtime answers.
// This passes the saved setting on before OpenXR starts and, when VR runs
// without eye textures, writes the plugin's own report to the log once: it
// names the runtime, installed API layers, extensions, formats and view sizes.
internal sealed class EyeTextureWatch : MonoBehaviour
{
    // Portable checks replace this; the game always calls the bundled plugin.
    internal static Action<string> WriteReport = NovrOpenXRReport.Dump;

    private float _vrSince = -1;
    private bool _reported;

    // Core enables this before it starts OpenXR, which reads the setting once per start.
    private void OnEnable()
    {
        _vrSince = -1;
        _reported = false;
        ApplySetting();
    }

    private void Update()
    {
        ApplySetting();
        if (!XRSettings.enabled)
        {
            _vrSince = -1;
            _reported = false;
            return;
        }
        if (_vrSince < 0)
            _vrSince = Time.unscaledTime;
        if (!EyeTexturePolicy.ShouldReport(true, XRSettings.eyeTextureWidth, XRSettings.eyeTextureHeight,
                Time.unscaledTime - _vrSince, _reported))
            return;
        _reported = true;
        // A headset that is asleep at launch also has none yet, so this states what was seen, not a fault.
        Debug.LogWarning("[NOVR] No eye textures after " + EyeTexturePolicy.ReportAfterSeconds +
            " seconds in VR. A headset that is awake then shows only the runtime's backdrop. Eye texture requests: " +
            NovrEyeTextureRequests.Status + " The OpenXR report follows.");
        try { WriteReport("NOVR: no eye textures"); }
        catch (Exception error)
        {
            // Never let a diagnostic aid interrupt the game.
            Debug.LogWarning("[NOVR] The OpenXR report is unavailable: " + error.GetType().Name);
        }
    }

    private static void ApplySetting()
    {
        var mode = EyeTexturePolicy.Normalize((int)ModConfiguration.Instance.EyeTextureRetry.Value);
        NovrEyeTextureRequests.Enabled = EyeTexturePolicy.Watches(mode);
        NovrEyeTextureRequests.EveryRuntime = EyeTexturePolicy.WatchesEveryRuntime(mode);
    }
}
