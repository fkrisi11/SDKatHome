#if UNITY_EDITOR
using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace SDKatHome.Patches
{
    /// <summary>
    /// Lets F2 rename two kinds of row the Animation window refuses to rename: properties recorded
    /// on the root object, and animated Animator parameters.
    ///
    /// Why both are blocked by the same line:
    ///   AnimationWindowHierarchyDataSource.IsRenamingItemAllowed ends with
    ///
    ///       return node.path.Length != 0;
    ///
    ///   Rename in the Animation window means "rename the curve's transform path", and a curve on
    ///   the root object has an empty path, so Unity declines. Animator parameters are curves on
    ///   the Animator component, which is on the root, so they have an empty path too.
    ///
    /// What rename means for each:
    ///   Root properties  keep Unity's own rename. The path field starts empty (the root has no
    ///                    path) and whatever is typed becomes the new path, moving the curves to
    ///                    that child. Only the gate is lifted; Unity's RenameEnded does the work.
    ///   Parameters       need a different rename entirely: the PROPERTY NAME, not the path.
    ///                    The rename field is seeded with the parameter name instead of the empty
    ///                    path, and on accept the curve is re-bound to the new parameter name via
    ///                    AnimationUtility, leaving path and type untouched.
    ///
    /// Everything reachable here is internal (AnimationWindowHierarchyNode, RenameOverlay, the
    /// GUI's m_RenamedNode), but every VALUE is a public type, so all access goes through
    /// reflection cached once at install.
    /// </summary>
    [HarmonyPatch]
    public class AnimationWindowRenaming : SDKPatchBase
    {
        public override string PatchName => "Animation Window Renaming";

        public override string Description => "Allows renaming root objects and animator parameters with F2";

        public override string Category => "Animator Tools";

        public override bool UsePrefix => false;
        public override bool UsePostfix => true;
        public override bool EnabledByDefault => true;

        public static MethodBase TargetMethod()
        {
            Type dataSourceType = AccessTools.TypeByName("UnityEditorInternal.AnimationWindowHierarchyDataSource");
            if (dataSourceType == null)
            {
                Warn("could not find AnimationWindowHierarchyDataSource.");
                return null;
            }

            // public override bool IsRenamingItemAllowed(TreeViewItem item)
            MethodInfo method = AccessTools.Method(dataSourceType, "IsRenamingItemAllowed", new[] { typeof(TreeViewItem) });
            if (method == null)
            {
                Warn("could not find AnimationWindowHierarchyDataSource.IsRenamingItemAllowed(TreeViewItem).");
                return null;
            }

            AnimationRenameBridge.Install();
            return method;
        }

        [HarmonyPostfix]
        public static void Postfix(TreeViewItem item, ref bool __result)
        {
            if (__result) return; // Unity already allows it
            if (!AnimationRenameBridge.Ready) return;
            if (!AnimationRenameBridge.IsRenameableRow(item)) return;

            __result = true;
        }

        internal static void Warn(string message)
        {
            Debug.LogWarning($"<color=#00FF00>[SDK at Home]</color> Animation Window Renaming: {message}");
        }
    }

    /// <summary>
    /// Reflection cache plus the two hooks that give Animator parameter rows their own rename
    /// semantics. Root-property rows need nothing here - once the gate is lifted, Unity's rename
    /// path handles them.
    /// </summary>
    internal static class AnimationRenameBridge
    {
        private const string HarmonyId = "com.tohruthedragon.sdkathome.animationwindowrenaming";

        // Cached so the per-row check is a plain dictionary hit; GetPatchInfo(Type) allocates via
        // Activator.CreateInstance on every call.
        private static readonly string PatchName = new AnimationWindowRenaming().PatchName;

        private static bool _installed;
        internal static bool Ready { get; private set; }

        // Node types
        private static Type _nodeType, _addButtonType, _masterType, _clipNodeType;
        private static FieldInfo _nodePath, _nodePropertyName, _nodeAnimatableType, _nodeCurves;

        // AnimationWindowCurve
        private static PropertyInfo _curveBinding, _curveClip;

        // AnimationWindowHierarchyGUI / TreeViewGUI
        private static FieldInfo _renamedNode;
        private static MethodInfo _getRenameOverlay;

        // RenameOverlay
        private static MethodInfo _overlayBeginRename;
        private static PropertyInfo _overlayName, _overlayOriginalName, _overlayAccepted;

        private static bool IsActive => SDKatHomePatcher.IsPatchActive(PatchName);

        #region Install

        internal static void Install()
        {
            if (_installed) return;
            _installed = true;

            try
            {
                _nodeType = AccessTools.TypeByName("UnityEditorInternal.AnimationWindowHierarchyNode");
                _addButtonType = AccessTools.TypeByName("UnityEditorInternal.AnimationWindowHierarchyAddButtonNode");
                _masterType = AccessTools.TypeByName("UnityEditorInternal.AnimationWindowHierarchyMasterNode");
                _clipNodeType = AccessTools.TypeByName("UnityEditorInternal.AnimationWindowHierarchyClipNode");
                Type curveType = AccessTools.TypeByName("UnityEditorInternal.AnimationWindowCurve");
                Type guiType = AccessTools.TypeByName("UnityEditorInternal.AnimationWindowHierarchyGUI");
                Type overlayType = AccessTools.TypeByName("UnityEditor.RenameOverlay");

                if (_nodeType == null || curveType == null || guiType == null || overlayType == null)
                {
                    AnimationWindowRenaming.Warn("could not resolve the Animation window's internal types; patch inactive.");
                    return;
                }

                _nodePath = AccessTools.Field(_nodeType, "path");
                _nodePropertyName = AccessTools.Field(_nodeType, "propertyName");
                _nodeAnimatableType = AccessTools.Field(_nodeType, "animatableObjectType");
                _nodeCurves = AccessTools.Field(_nodeType, "curves");

                _curveBinding = AccessTools.Property(curveType, "binding");
                _curveClip = AccessTools.Property(curveType, "clip");

                _renamedNode = AccessTools.Field(guiType, "m_RenamedNode");
                _getRenameOverlay = AccessTools.Method(guiType, "GetRenameOverlay", Type.EmptyTypes);

                _overlayBeginRename = AccessTools.Method(overlayType, "BeginRename",
                    new[] { typeof(string), typeof(int), typeof(float) });
                _overlayName = AccessTools.Property(overlayType, "name");
                _overlayOriginalName = AccessTools.Property(overlayType, "originalName");
                _overlayAccepted = AccessTools.Property(overlayType, "userAcceptedRename");

                if (_nodePath == null || _nodePropertyName == null || _nodeAnimatableType == null || _nodeCurves == null ||
                    _curveBinding == null || _curveClip == null || _renamedNode == null || _getRenameOverlay == null ||
                    _overlayBeginRename == null || _overlayName == null || _overlayOriginalName == null || _overlayAccepted == null)
                {
                    AnimationWindowRenaming.Warn("an Animation window internal member was not found; patch inactive.");
                    return;
                }

                MethodInfo beginRename = AccessTools.Method(guiType, "BeginRename", new[] { typeof(TreeViewItem), typeof(float) });
                MethodInfo renameEnded = AccessTools.Method(guiType, "RenameEnded", Type.EmptyTypes);

                if (beginRename == null || renameEnded == null)
                {
                    AnimationWindowRenaming.Warn("could not find BeginRename/RenameEnded on the hierarchy GUI; patch inactive.");
                    return;
                }

                var harmony = new Harmony(HarmonyId);
                harmony.Patch(beginRename, Hook(nameof(BeginRenamePrefix)));
                harmony.Patch(renameEnded, Hook(nameof(RenameEndedPrefix)));

                Ready = true;
            }
            catch (Exception e)
            {
                AnimationWindowRenaming.Warn($"install failed, Animation window left stock: {e.Message}");
                Ready = false;
            }
        }

        private static HarmonyMethod Hook(string name)
        {
            var method = typeof(AnimationRenameBridge).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null) throw new MissingMethodException($"AnimationRenameBridge.{name} not found");
            return new HarmonyMethod(method);
        }

        #endregion

        #region Row classification

        /// <summary>
        /// The rows Unity declines purely for having an empty path. Unity's other refusals -
        /// the add button, the master row, the clip row - are kept.
        /// </summary>
        internal static bool IsRenameableRow(TreeViewItem item)
        {
            if (!IsActive || item == null) return false;
            if (!_nodeType.IsInstanceOfType(item)) return false;

            if (_addButtonType != null && _addButtonType.IsInstanceOfType(item)) return false;
            if (_masterType != null && _masterType.IsInstanceOfType(item)) return false;
            if (_clipNodeType != null && _clipNodeType.IsInstanceOfType(item)) return false;

            return true;
        }

        /// <summary>An animated Animator parameter: a curve whose target component is the Animator.</summary>
        private static bool IsParameterRow(object item)
        {
            if (item == null || !_nodeType.IsInstanceOfType(item)) return false;
            return (Type)_nodeAnimatableType.GetValue(item) == typeof(Animator);
        }

        #endregion

        #region Parameter rename

        /// <summary>
        /// Unity seeds the rename field with node.path, which for a parameter is empty. Replicate
        /// its two lines with the parameter name instead. Root-property rows fall through to stock.
        /// </summary>
        private static bool BeginRenamePrefix(object __instance, TreeViewItem item, float delay, ref bool __result)
        {
            if (!IsActive || !IsParameterRow(item)) return true;

            try
            {
                _renamedNode.SetValue(__instance, item);

                object overlay = _getRenameOverlay.Invoke(__instance, null);
                string parameterName = (string)_nodePropertyName.GetValue(item) ?? "";

                __result = (bool)_overlayBeginRename.Invoke(overlay, new object[] { parameterName, item.id, delay });
                return false;
            }
            catch (Exception e)
            {
                AnimationWindowRenaming.Warn($"could not start a parameter rename: {e.Message}");
                return true;
            }
        }

        /// <summary>
        /// Unity's RenameEnded would treat the typed text as a new PATH. For a parameter row we
        /// re-bind the curve to a new property name instead, then clear the renamed-node marker
        /// exactly as Unity does.
        /// </summary>
        private static bool RenameEndedPrefix(object __instance)
        {
            if (!IsActive) return true;

            object node = _renamedNode.GetValue(__instance);
            if (!IsParameterRow(node)) return true;

            try
            {
                object overlay = _getRenameOverlay.Invoke(__instance, null);
                bool accepted = (bool)_overlayAccepted.GetValue(overlay, null);
                string newName = (string)_overlayName.GetValue(overlay, null);
                string oldName = (string)_overlayOriginalName.GetValue(overlay, null);

                if (accepted && !string.IsNullOrEmpty(newName) && newName != oldName)
                    RenameParameter(node, newName);
            }
            catch (Exception e)
            {
                AnimationWindowRenaming.Warn($"parameter rename failed: {e.Message}");
            }
            finally
            {
                _renamedNode.SetValue(__instance, null);
            }

            return false;
        }

        private static void RenameParameter(object node, string newName)
        {
            var curves = _nodeCurves.GetValue(node) as IEnumerable;
            if (curves == null) return;

            foreach (object curve in curves)
            {
                if (curve == null) continue;

                var binding = (EditorCurveBinding)_curveBinding.GetValue(curve, null);
                var clip = _curveClip.GetValue(curve, null) as AnimationClip;
                if (clip == null) continue;

                EditorCurveBinding target = binding;
                target.propertyName = newName;

                // Mirrors Unity's own "Curve already exists, renaming cancelled." behaviour.
                if (AnimationUtility.GetEditorCurve(clip, target) != null)
                {
                    Debug.LogWarning($"<color=#00FF00>[SDK at Home]</color> A curve for parameter '{newName}' already exists, renaming cancelled.");
                    continue;
                }

                AnimationCurve data = AnimationUtility.GetEditorCurve(clip, binding);
                if (data == null) continue;

                Undo.RecordObject(clip, "Rename Animator Parameter");
                AnimationUtility.SetEditorCurve(clip, binding, null);
                AnimationUtility.SetEditorCurve(clip, target, data);
            }
        }

        #endregion
    }
}
#endif
