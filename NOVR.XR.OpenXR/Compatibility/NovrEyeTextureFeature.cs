using System;
using System.Collections.Generic;
using UnityEngine.XR.OpenXR.Features;

namespace UnityEngine.XR.OpenXR
{
    // Hands the plugin's OpenXR function lookup to NovrEyeTextureRequests each
    // time the loader starts. It requests no extension and owns no state.
    public sealed class NovrEyeTextureFeature : OpenXRFeature
    {
        internal const string Id = "novr.openxr.feature.eyetexturerequests";

        protected internal override IntPtr HookGetInstanceProcAddr(IntPtr func) =>
            NovrEyeTextureRequests.Hook(func, Debug.Log);
    }

    public partial class OpenXRSettings
    {
        // Like the controller profiles, registered before loader initialization:
        // the plugin resolves xrCreateSwapchain once, when the instance is created.
        public void EnableNovrEyeTextureRequests()
        {
            var all = new List<OpenXRFeature>(features);
            var feature = all.Find(item => item is NovrEyeTextureFeature);
            if (feature == null)
            {
                feature = CreateInstance<NovrEyeTextureFeature>();
                feature.nameUi = "NOVR Eye Texture Requests";
                feature.version = "1";
                feature.featureIdInternal = NovrEyeTextureFeature.Id;
                feature.company = "NOVR";
                feature.openxrExtensionStrings = "";
                feature.required = false;
                all.Add(feature);
            }
            feature.enabled = true;
            features = all.ToArray();
        }
    }

    // The plugin writes its diagnostic report only when a session reaches focus
    // or the loader shuts down. A session that never shows a frame does neither.
    public static class NovrOpenXRReport
    {
        public static void Dump(string reason) => DiagnosticReport.DumpReport(reason);
    }
}
