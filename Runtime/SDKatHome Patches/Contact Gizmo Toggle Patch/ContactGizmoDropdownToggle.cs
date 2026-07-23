#if UNITY_EDITOR
using System;
using System.Reflection;
using HarmonyLib;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;                              // ContactBase
using VRC.SDK3.Dynamics.Contact.Components;      // VRCContactReceiver, VRCContactSender

namespace SDKatHome.Patches
{
    /// <summary>
    /// Makes Contact Sender / Contact Receiver gizmos toggleable in Unity's Gizmos dropdown.
    ///
    /// Problem:
    ///   VRCContactBaseEditor draws its gizmos from [DrawGizmo] methods whose target parameter is
    ///   the ABSTRACT base type VRC.Dynamics.ContactBase (OnDrawGizmo_Full / OnDrawGizmo_Half).
    ///   Unity's Gizmos-dropdown checkboxes are keyed off concrete MonoScripts, and it also gates
    ///   invocation of a [DrawGizmo] method by that type's checkbox. Because the registration is
    ///   against an abstract type with no MonoScript, no checkbox is created and the gizmos are not
    ///   toggleable — unlike PhysBone / PhysBoneCollider which register against concrete types and
    ///   therefore DO get working toggles.
    ///
    /// Fix (two parts):
    ///   1. Register our own [DrawGizmo] methods against the CONCRETE VRCContactReceiver and
    ///      VRCContactSender types. That is what makes "VRCContactReceiver" / "VRCContactSender"
    ///      appear (and gate) in the Gizmos dropdown. Those methods just re-invoke the SDK's own
    ///      OnDrawGizmo_Full / OnDrawGizmo_Half so the drawing is pixel-identical and stays in sync
    ///      with SDK updates.
    ///   2. Suppress the SDK's original ContactBase draw so we don't render twice. We prefix the
    ///      shared inner VRCContactBaseEditor.DrawGizmo(ContactBase) helper: it runs only when the
    ///      call came from our bridge, and is skipped when Unity invokes the SDK's ContactBase path
    ///      directly.
    ///
    /// Everything is keyed on IsPatchActive(this patch): when the patch is off, the prefix is not
    /// installed AND the bridge no-ops, so the SDK behaves exactly as stock.
    /// </summary>
    [HarmonyPatch]
    public class ContactGizmoDropdownToggle : SDKPatchBase
    {
        public override string PatchName => "Toggleable Contact Gizmos";
        public override string Description =>
            "Adds Gizmos-dropdown checkboxes for Contact Sender and Contact Receiver.";
        public override string Category => "Avatar Tools";
        public override bool UsePrefix => true;
        public override bool UsePostfix => false;
        public override bool EnabledByDefault => true;

        public static MethodBase TargetMethod()
        {
            // private static void VRCContactBaseEditor.DrawGizmo(ContactBase target)
            var type = AccessTools.TypeByName("VRC.SDK3.Dynamics.Contact.VRCContactBaseEditor");
            if (type == null)
            {
                Debug.LogWarning("<color=#00FF00>[SDK at Home]</color> Toggleable Contact Gizmos: could not find VRCContactBaseEditor.");
                return null;
            }

            var inner = AccessTools.Method(type, "DrawGizmo", new[] { typeof(ContactBase) });
            if (inner == null)
            {
                Debug.LogWarning("<color=#00FF00>[SDK at Home]</color> Toggleable Contact Gizmos: could not find DrawGizmo(ContactBase).");
                return null;
            }

            // Resolve the outer methods our bridge re-invokes while we're here.
            ContactGizmoBridge.Init(type);
            return inner;
        }

        [HarmonyPrefix]
        public static bool Prefix()
        {
            // Run the original inner draw only when it originates from our concrete-typed bridge.
            // Direct SDK (ContactBase) invocations are suppressed to avoid double-drawing.
            // If the bridge failed to resolve, never suppress — fall back to stock SDK behavior.
            return ContactGizmoBridge.DrawingFromBridge || !ContactGizmoBridge.Healthy;
        }
    }

    /// <summary>
    /// Re-invokes the SDK's own contact gizmo methods so our concrete-typed [DrawGizmo] entries
    /// render identically, while a re-entrancy flag lets the suppression prefix tell our draws
    /// apart from the SDK's own ContactBase draws.
    /// </summary>
    internal static class ContactGizmoBridge
    {
        [ThreadStatic] public static bool DrawingFromBridge;
        public static bool Healthy { get; private set; }

        private static Action<ContactBase, GizmoType> _full;
        private static Action<ContactBase, GizmoType> _half;
        private static bool _initialized;

        // Cached once so the per-gizmo enabled check is a cheap dictionary lookup
        // (IsPatchActive(Type) allocates via Activator.CreateInstance on every call).
        private static readonly string _patchName = new ContactGizmoDropdownToggle().PatchName;

        public static bool Enabled => SDKatHomePatcher.IsPatchActive(_patchName);

        public static void Init(Type editorType)
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                var full = AccessTools.Method(editorType, "OnDrawGizmo_Full", new[] { typeof(ContactBase), typeof(GizmoType) });
                var half = AccessTools.Method(editorType, "OnDrawGizmo_Half", new[] { typeof(ContactBase), typeof(GizmoType) });

                if (full == null || half == null)
                {
                    Debug.LogWarning("<color=#00FF00>[SDK at Home]</color> Toggleable Contact Gizmos: could not resolve the SDK's OnDrawGizmo_Full/_Half.");
                    return;
                }

                _full = (Action<ContactBase, GizmoType>)Delegate.CreateDelegate(typeof(Action<ContactBase, GizmoType>), full);
                _half = (Action<ContactBase, GizmoType>)Delegate.CreateDelegate(typeof(Action<ContactBase, GizmoType>), half);
                Healthy = true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"<color=#00FF00>[SDK at Home]</color> Toggleable Contact Gizmos: bridge init failed: {e.Message}");
                Healthy = false;
            }
        }

        public static void Draw(ContactBase contact, GizmoType gizmoType, bool full)
        {
            // Only take over drawing when the patch is active; otherwise the SDK's own (un-suppressed)
            // ContactBase gizmos are still running and we must not draw a second time.
            if (!Healthy || !Enabled || contact == null) return;

            DrawingFromBridge = true;
            try
            {
                if (full) _full(contact, gizmoType);
                else _half(contact, gizmoType);
            }
            finally
            {
                DrawingFromBridge = false;
            }
        }
    }

    /// <summary>
    /// Concrete-typed [DrawGizmo] registrations. Their existence is what gives Unity the
    /// per-type Gizmos-dropdown checkboxes for Contact Receiver / Contact Sender; Unity only
    /// invokes them when that checkbox is enabled. GizmoType flags mirror the SDK's originals
    /// (Full = Selected|Active, Half = InSelectionHierarchy).
    /// </summary>
    internal static class ContactGizmoRegistrar
    {
        [DrawGizmo(GizmoType.Selected | GizmoType.Active, typeof(VRCContactReceiver))]
        private static void ReceiverFull(VRCContactReceiver c, GizmoType t) => ContactGizmoBridge.Draw(c, t, true);

        [DrawGizmo(GizmoType.InSelectionHierarchy, typeof(VRCContactReceiver))]
        private static void ReceiverHalf(VRCContactReceiver c, GizmoType t) => ContactGizmoBridge.Draw(c, t, false);

        [DrawGizmo(GizmoType.Selected | GizmoType.Active, typeof(VRCContactSender))]
        private static void SenderFull(VRCContactSender c, GizmoType t) => ContactGizmoBridge.Draw(c, t, true);

        [DrawGizmo(GizmoType.InSelectionHierarchy, typeof(VRCContactSender))]
        private static void SenderHalf(VRCContactSender c, GizmoType t) => ContactGizmoBridge.Draw(c, t, false);
    }
}
#endif
