using NOVR.Performance;
using UnityEngine;

namespace NOVR.VrUi;

internal sealed partial class WristMenu
{
    // The readout sits beside the switches it helps judge, so a change can be
    // compared in the same scene without leaving the page.
    private GameObject BuildFrameTimingPage()
    {
        var page = Page("FRAME TIMING");
        var parent = page.transform;
        var settings = ModConfiguration.Instance.Performance;
        Section(parent, "Recent frames", 214);
        var status = ViewMenuControls.Text(parent, _font!, "", new Vector2(0, 104), new Vector2(608, 176), 16);
        _liveLabels.Add((status, () => FrameTimingRuntime.Status));
        Section(parent, "Tuning", -6);
        var limit = ViewMenuControls.Checkbox(parent, _font!, "Hold off the game's frame limit in VR", new Vector2(0, -50), 620,
            () => settings.HoldOffFrameLimit.Value, value => settings.HoldOffFrameLimit.Value = value);
        var shadows = ViewMenuControls.Checkbox(parent, _font!, "Share shadows between eyes (experimental)", new Vector2(0, -96), 620,
            () => settings.ShareShadows.Value, value => settings.ShareShadows.Value = value);
        var log = ViewMenuControls.Checkbox(parent, _font!, "Log a timing summary every 10 seconds", new Vector2(0, -142), 620,
            () => settings.LogFrameTiming.Value, value => settings.LogFrameTiming.Value = value);
        _settingRefresh.Add((page, () =>
        {
            limit.Refresh();
            shadows.Refresh();
            log.Refresh();
        }
        ));
        Note(parent, "Shared shadows draw the sun's shadows once per frame.\nTurn off if shadows flicker or differ between eyes.", -205, 56);
        return page;
    }
}
