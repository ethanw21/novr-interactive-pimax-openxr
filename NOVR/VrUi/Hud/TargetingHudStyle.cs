using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrUi.SpecialBehavior;

internal sealed class TargetingHudStyle : MonoBehaviour
{
    private static readonly FieldInfo TargetTextField = AccessTools.Field(typeof(CombatHUD), "targetText");
    private static readonly FieldInfo TargetInfoField = AccessTools.Field(typeof(CombatHUD), "targetInfo");
    private static readonly FieldInfo TargetArrowField = AccessTools.Field(typeof(CombatHUD), "targetArrow");
    private static readonly Dictionary<Type, GraphicField[]> FieldsByType = new();
    private static readonly Predicate<Graphic> IsDestroyed = graphic => graphic == null;
    private readonly HashSet<Graphic> _styled = new();
    private readonly List<HUDWeaponState> _states = new();
    private readonly List<Graphic> _graphics = new();
    private float _nextScan;
    private CombatHUD? _labelOwner;
    private HudElementStyle? _targetTextStyle, _targetInfoStyle;

    private void UpdateAircraftLabels(CombatHUD? combat)
    {
        if (_labelOwner != combat)
        {
            RestoreAircraftLabels();
            _labelOwner = combat;
            _targetTextStyle = combat == null ? null : HudElementStyle.Add(TargetTextField.GetValue(combat) as TMP_Text, HudElementKind.Label);
            _targetInfoStyle = combat == null ? null : HudElementStyle.Add(TargetInfoField.GetValue(combat) as TMP_Text, HudElementKind.Label);
        }
        var targets = combat?.GetTargetList();
        var affiliation = LabelAffiliation(targets != null && targets.Count > 0 ? targets[0] : null);
        // These are shared native labels for the first selected target, not
        // children of each HUDUnitMarker. Do not infer affiliation from color
        // (selection changes it) or scale every marker during its update loop.
        if (_targetTextStyle != null)
            _targetTextStyle.SetAircraftLabelAffiliation(affiliation);
        if (_targetInfoStyle != null)
            _targetInfoStyle.SetAircraftLabelAffiliation(affiliation);
    }

    internal static AircraftLabelAffiliation LabelAffiliation(Unit? unit) => unit is Aircraft
        ? DynamicMap.GetFactionMode(unit.NetworkHQ) switch
        {
            FactionMode.Friendly => AircraftLabelAffiliation.Friendly,
            FactionMode.Enemy => AircraftLabelAffiliation.Hostile,
            _ => AircraftLabelAffiliation.Other
        }
        : AircraftLabelAffiliation.Other;

    private void RestoreAircraftLabels()
    {
        if (_targetTextStyle != null)
            _targetTextStyle.SetAircraftLabelAffiliation(AircraftLabelAffiliation.Other);
        if (_targetInfoStyle != null)
            _targetInfoStyle.SetAircraftLabelAffiliation(AircraftLabelAffiliation.Other);
    }
    private void OnDisable() => RestoreAircraftLabels();

    private void LateUpdate()
    {
        var combat = SceneSingleton<CombatHUD>.i;
        UpdateAircraftLabels(combat);
        if (Time.unscaledTime < _nextScan)
            return;
        _nextScan = Time.unscaledTime + .5f;
        _styled.RemoveWhere(IsDestroyed);
        var flight = SceneSingleton<FlightHud>.i;
        if (flight != null)
        {
            Style(flight.velocityVector, HudElementKind.Reticle);
            Style(flight.waterline, HudElementKind.Reticle);
        }
        if (combat == null)
            return;
        Style(combat.targetDesignator, HudElementKind.Reticle);
        Style(TargetArrowField.GetValue(combat) as Graphic, HudElementKind.Marker);
        if (flight == null)
            return;
        _states.Clear();
        flight.GetComponentsInChildren(true, _states);
        foreach (var state in _states)
        {
            if (state == null)
                continue;
            var widget = state is HUDMissileState || state is HUDLaserGuidedState || state is HUDCargoState;
            if (widget)
            {
                // Scale every part around the authored widget origin, including
                // static labels/artwork that have no serialized script field.
                // Target-designator objects are outside this hierarchy.
                _graphics.Clear();
                state.GetComponentsInChildren(true, _graphics);
                foreach (var graphic in _graphics)
                    // TMP's parent callback already styles every material mesh.
                    if (graphic is not TMP_SubMeshUI)
                        Style(graphic, HudElementKind.Weapon, state.transform);
                continue;
            }
            foreach (var field in GraphicFields(state.GetType()))
            {
                if (field.Array)
                {
                    if (field.Info.GetValue(state) is Graphic[] graphics)
                        foreach (var g in graphics)
                            Register(state, field.Kind, g);
                }
                else
                    Register(state, field.Kind, field.Info.GetValue(state) as Graphic);
            }
        }
        _states.Clear();
        _graphics.Clear();
    }

    private void Register(HUDWeaponState state, HudElementKind kind, Graphic? graphic)
    {
        // Shared target-designator graphics belong to the flight HUD.
        if (graphic == null || _styled.Contains(graphic) || !graphic.transform.IsChildOf(state.transform))
            return;
        Style(graphic, graphic is TMP_Text ? HudElementKind.Label : kind);
    }

    // A style component is permanent once attached. This scan repeats twice a
    // second to catch late native graphics, so remember what it has already
    // styled instead of looking every component up again each time.
    private void Style(Graphic? graphic, HudElementKind kind, Transform? group = null)
    {
        if (graphic == null || !_styled.Add(graphic))
            return;
        HudElementStyle.Add(graphic, kind, group);
    }

    private static GraphicField[] GraphicFields(Type type)
    {
        if (FieldsByType.TryGetValue(type, out var known))
            return known;
        var found = new List<GraphicField>();
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
            var array = typeof(Graphic[]).IsAssignableFrom(field.FieldType);
            if (!array && !typeof(Graphic).IsAssignableFrom(field.FieldType))
                continue;
            var kind = field.Name == "line" ? HudElementKind.HorizontalLine :
                field.Name == "ccipLine" || field.Name == "alignmentBar" ? HudElementKind.VerticalLine : HudElementKind.Reticle;
            found.Add(new GraphicField(field, array, kind));
        }
        return FieldsByType[type] = found.ToArray();
    }

    private readonly struct GraphicField
    {
        internal readonly FieldInfo Info;
        internal readonly bool Array;
        internal readonly HudElementKind Kind;
        internal GraphicField(FieldInfo info, bool array, HudElementKind kind)
        {
            Info = info;
            Array = array;
            Kind = kind;
        }
    }
}
