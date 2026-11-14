using HarmonyLib;
using NOVR.VrUi.SpecialBehavior;
using NuclearOption.UIStyleSystem;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrUi.HarmonyPatches;


// Ensures our hud markers are in our VR UI camera's space.
// This replaces the game's update for every marker on every frame, so it reads
// private state through Harmony's injected fields and typed accessors, and takes
// the camera poses once per marker loop instead of once per marker.
internal static class HUDUnitMarkerViewPositionPatch
{
    private static readonly AccessTools.FieldRef<global::CombatHUD, Image>? TargetArrow = FieldRef<Image>("targetArrow");
    private static readonly AccessTools.FieldRef<global::CombatHUD, Transform>? TargetArrowTail = FieldRef<Transform>("targetArrowTail");
    private static readonly AccessTools.FieldRef<global::CombatHUD, TextMeshProUGUI>? TargetText = FieldRef<TextMeshProUGUI>("targetText");
    private static readonly AccessTools.FieldRef<global::CombatHUD, TextMeshProUGUI>? TargetInfo = FieldRef<TextMeshProUGUI>("targetInfo");
    private static VrHudProjection.Frame _frame;
    private static Camera? _cockpitHudCamera;
    private static int _loop, _frameLoop = -1;
    private static bool _inLoop;

    private static AccessTools.FieldRef<global::CombatHUD, T>? FieldRef<T>(string name)
    {
        try
        {
            return AccessTools.FieldRefAccess<global::CombatHUD, T>(name);
        }
        catch (System.Exception exception)
        {
            Debug.LogWarning($"NOVR: CombatHUD.{name} is unavailable; the selected-target arrow keeps its native placement. {exception.Message}");
            return null;
        }
    }

    // One snapshot serves the whole native marker loop. A marker updated
    // outside that loop, such as from its constructor, takes its own.
    private static bool RefreshFrame()
    {
        if (_inLoop && _frameLoop == _loop)
            return _frame.Valid;
        _frameLoop = _loop;
        _cockpitHudCamera = APIBus.CockpitHudCamera;
        _frame = VrHudProjection.Frame.Capture(APIBus.MainCamera, _cockpitHudCamera);
        if (!_frame.Valid)
            return false;
        var combat = SceneSingleton<CombatHUD>.i;
        var targetInfo = combat == null || TargetInfo == null ? null : TargetInfo(combat);
        if (targetInfo != null)
            targetInfo.transform.rotation = _frame.HudRotation;
        return true;
    }

    [HarmonyPatch(typeof(global::CombatHUD), "UpdateMarkers")]
    private static class MarkerLoopPatch
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            _loop++;
            _inLoop = true;
        }

        [HarmonyFinalizer]
        private static void Finalizer() => _inLoop = false;
    }

    // The style component is permanent, so attach it when the marker is made
    // instead of looking it up again for every marker on every frame.
    [HarmonyPatch(typeof(global::HUDUnitMarker), MethodType.Constructor, typeof(global::Unit), typeof(Image))]
    private static class MarkerCreatedPatch
    {
        [HarmonyPostfix]
        private static void Postfix(global::HUDUnitMarker __instance) =>
            HudElementStyle.Add(__instance.image, HudElementKind.Marker);
    }

    [HarmonyPatch(typeof(global::HUDUnitMarker), nameof(global::HUDUnitMarker.UpdatePosition))]
    private static class UpdatePositionPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(global::HUDUnitMarker __instance, FactionHQ hq, bool ___hidden, Transform ____transform,
            Sprite ___icon, float ___timeCreated, Color ___color, bool ___flashing)
        {
            if (!RefreshFrame())
                return true;

            var cockpitHudCamera = _cockpitHudCamera!;
            var markerTransform = ____transform;
            markerTransform.rotation = _frame.HudRotation;

            if (___hidden)
                return false;

            GlobalPosition knownPosition = __instance.unit.GlobalPosition();
            if (__instance.outdated && !hq.TryGetKnownPosition(__instance.unit, out knownPosition))
                return false;

            var knownWorldPosition = knownPosition.ToLocalPosition();
            if (__instance.selected)
            {
                if (VrHudProjection.PinToScreenEdge(knownWorldPosition, out var rayToScreen, out _))
                {
                    __instance.image.enabled = false;
                    if (VrHudProjection.TryProjectDirectionToCockpitHud(knownWorldPosition, out var targetHudPosition))
                        SetTargetArrow(SceneSingleton<CombatHUD>.i, true, rayToScreen, targetHudPosition,
                            -cockpitHudCamera.transform.forward, cockpitHudCamera);
                }
                else
                {
                    __instance.image.enabled = true;
                    if (_frame.TryProject(knownWorldPosition, out var targetHudPosition))
                        markerTransform.position = targetHudPosition;
                    SetTargetArrow(SceneSingleton<CombatHUD>.i, false, Vector3.zero, Vector3.zero, Vector3.zero,
                        cockpitHudCamera);
                }

                if (!__instance.unit.HasRadarEmission())
                    return false;

                if (__instance.unit.radar is Radar radar && radar.IsJammed())
                {
                    if (__instance.image.sprite == GameAssets.i.targetUnitSpriteJammed)
                        return false;
                    __instance.image.sprite = GameAssets.i.targetUnitSpriteJammed;
                }
                else
                {
                    if (__instance.image.sprite != GameAssets.i.targetUnitSpriteJammed)
                        return false;
                    __instance.image.sprite = DynamicMap.GetFactionMode(__instance.unit.NetworkHQ) == FactionMode.Friendly
                        ? GameAssets.i.targetUnitSpriteFriendly
                        : ___icon;
                }
            }
            else if (_frame.IsBehind(knownWorldPosition))
            {
                if (!__instance.image.enabled)
                    return false;
                __instance.image.enabled = false;
            }
            else
            {
                if (!__instance.image.enabled)
                    __instance.image.enabled = true;
                if (_frame.TryProject(knownWorldPosition, out var targetHudPosition))
                    markerTransform.position = targetHudPosition;

                if (__instance.fresh)
                {
                    var warningColor = ThemeManager.Active.ColorTheme.Warning;
                    var t = Time.timeSinceLevelLoad - ___timeCreated;
                    __instance.image.color = Color.Lerp(___color + warningColor, ___color, t);
                    if (t > 1.0f)
                        __instance.fresh = false;
                }

                if (!___flashing)
                    return false;
                var flashingWarningColor = ThemeManager.Active.ColorTheme.Warning;
                __instance.image.color = Color.Lerp(
                    ___color + flashingWarningColor,
                    ___color,
                    Mathf.Sin(Time.timeSinceLevelLoad * 20f) + 0.5f);
            }

            return false;
        }

        private static void SetTargetArrow(global::CombatHUD instance, bool enabled, Vector3 position, Vector3 targetPosition, Vector3 up, Component screenSpaceCamera)
        {
            if (instance == null || TargetArrow == null || TargetArrowTail == null || TargetText == null)
                return;
            var targetArrow = TargetArrow(instance);
            var targetArrowTail = TargetArrowTail(instance);
            var targetText = TargetText(instance);
            if (targetArrow == null || targetArrowTail == null || targetText == null)
                return;

            targetArrow.enabled = enabled;
            targetText.enabled = enabled;
            targetText.transform.rotation = screenSpaceCamera.transform.rotation;
            if (!enabled)
                return;

            targetArrow.transform.position = position;
            targetText.transform.position = targetArrowTail.position;
            var desiredUp = targetPosition - position;
            if (desiredUp.sqrMagnitude <= Mathf.Epsilon)
                desiredUp = targetArrow.transform.up;
            desiredUp.Normalize();

            var desiredForward = -up;
            if (desiredForward.sqrMagnitude <= Mathf.Epsilon)
                desiredForward = APIBus.CockpitHudCamera.transform.forward;

            desiredForward = Vector3.ProjectOnPlane(desiredForward, desiredUp);
            if (desiredForward.sqrMagnitude <= Mathf.Epsilon)
                desiredForward = Vector3.ProjectOnPlane(APIBus.CockpitHudCamera.transform.forward, desiredUp);
            if (desiredForward.sqrMagnitude <= Mathf.Epsilon)
                desiredForward = Vector3.Cross(desiredUp, APIBus.CockpitHudCamera.transform.right);

            targetArrow.transform.rotation = Quaternion.LookRotation(desiredForward.normalized, desiredUp);
        }
    }

}
