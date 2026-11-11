namespace NOVR.MotionControls;

public enum ControllerButton
{
    None, Trigger, Primary, Secondary, Menu, Shoulder, Top, Outside, StickClick,
    ShoulderOrPrimary, OutsideOrSecondary, PrimaryOrStickClick, PrimaryOrSecondary, SecondaryOrStickClick, SecondaryOrNone,
    TopOrSecondary, OutsideOrNone,
    // Valve Index only. Appended so saved bindings keep their existing names.
    Trackpad
}

// The in-headset help uses the same hand/profile choices as input sampling.
internal static class ControllerButtonNames
{
    internal static ControllerButton Resolve(ControllerButton button, bool frame) => button switch
    {
        ControllerButton.ShoulderOrPrimary => frame ? ControllerButton.Shoulder : ControllerButton.Primary,
        ControllerButton.OutsideOrSecondary => frame ? ControllerButton.Outside : ControllerButton.Secondary,
        ControllerButton.PrimaryOrStickClick => frame ? ControllerButton.Primary : ControllerButton.StickClick,
        ControllerButton.PrimaryOrSecondary => frame ? ControllerButton.Primary : ControllerButton.Secondary,
        ControllerButton.SecondaryOrStickClick => frame ? ControllerButton.Secondary : ControllerButton.StickClick,
        ControllerButton.SecondaryOrNone => frame ? ControllerButton.Secondary : ControllerButton.None,
        ControllerButton.TopOrSecondary => frame ? ControllerButton.Top : ControllerButton.Secondary,
        ControllerButton.OutsideOrNone => frame ? ControllerButton.Outside : ControllerButton.None,
        _ => button
    };
    internal static bool SamePhysicalButton(ControllerButton first, ControllerButton second, bool frame)
    {
        var resolved = Resolve(first, frame);
        return resolved != ControllerButton.None && resolved == Resolve(second, frame);
    }
    // Index shares Touch's physical positions, with A/B on both hands, a
    // force-sensing trackpad and no application menu button.
    internal static string Describe(ControllerButton button, bool left, bool frame, bool index = false)
    {
        button = Resolve(button, frame);
        if (index)
            return button switch
            {
                ControllerButton.Trigger => "Trigger",
                ControllerButton.StickClick => "Thumbstick click",
                ControllerButton.Primary => "A",
                ControllerButton.Secondary => "B",
                ControllerButton.Trackpad => "Trackpad press",
                ControllerButton.Menu => "Unavailable (system button)",
                ControllerButton.None => "Unassigned",
                _ => "Unavailable"
            };
        return button switch
        {
            ControllerButton.Trigger => "Trigger",
            ControllerButton.StickClick => "Thumbstick click",
            ControllerButton.Primary => frame ? (left ? "D-pad down" : "A") : (left ? "X" : "A"),
            ControllerButton.Secondary => frame ? (left ? "D-pad right" : "X") : (left ? "Y" : "B"),
            ControllerButton.Menu => frame ? (left ? "View" : "Menu") : left ? "Menu" : "Unavailable (system button)",
            ControllerButton.Shoulder => frame ? "Bumper" : "Unavailable",
            ControllerButton.Top => frame ? (left ? "D-pad up" : "Y") : "Unavailable",
            ControllerButton.Outside => frame ? (left ? "D-pad left" : "B") : "Unavailable",
            ControllerButton.Trackpad => "Unavailable",
            _ => "Unassigned"
        };
    }
}
