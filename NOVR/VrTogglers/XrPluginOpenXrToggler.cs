
using UnityEngine;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;

namespace NOVR.VrTogglers;

public class XrPluginOpenXrToggler : XrPluginToggler
{
    protected override XRLoader CreateLoader()
    {
        // On the first install this assembly may be copied after the preloader has
        // enumerated patch targets. Configure stereo before loader initialization too.
        OpenXRSettings.Instance.renderMode = OpenXRSettings.RenderMode.MultiPass;
        OpenXRSettings.Instance.EnableNovrControllerProfiles();
        // The plugin looks up its runtime functions once per start, so this
        // must be registered first as well. EyeTextureWatch owns the setting.
        OpenXRSettings.Instance.EnableNovrEyeTextureRequests();
        var xrLoader = ScriptableObject.CreateInstance<OpenXRLoader>();
        return xrLoader;
    }
}
