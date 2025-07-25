#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using HarmonyLib;
using System.Reflection;
using System.Linq;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace SDKatHome.Patches
{
    [SDKPatch("PhysBone Auto-Update in Play Mode",
              "Automatically updates PhysBones when values are changed during play mode",
              "Avatar Tools",
              usePrefix: true,
              usePostfix: true,
              enabledByDefault:false)]
    [HarmonyPatch]
    public class PhysBoneAutoUpdate
    {
        private static System.Type physBoneEditorType;

        public static MethodBase TargetMethod()
        {
            // Try to find the VRCPhysBoneEditor type using reflection
            physBoneEditorType = FindPhysBoneEditorType();

            if (physBoneEditorType == null)
            {
                //Debug.LogWarning("<color=#FF6600>[SDK at Home]</color> Could not find VRCPhysBoneEditor type");
                return null;
            }

            var method = physBoneEditorType.GetMethod("OnInspectorGUI",
                BindingFlags.Public | BindingFlags.Instance);

            if (method == null)
            {
                //Debug.LogWarning("<color=#FF6600>[SDK at Home]</color> Could not find OnInspectorGUI method in VRCPhysBoneEditor");
            }
            else
            {
                //Debug.Log("<color=#00FF00>[SDK at Home]</color> Successfully found VRCPhysBoneEditor.OnInspectorGUI for patching");
            }

            return method;
        }

        private static System.Type FindPhysBoneEditorType()
        {
            // First try the expected namespace
            var type = System.Type.GetType("VRC.SDK3.Dynamics.PhysBone.VRCPhysBoneEditor");
            if (type != null) return type;

            // Search through all loaded assemblies
            foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    // Look for types containing "PhysBoneEditor"
                    var types = assembly.GetTypes().Where(t =>
                        t.Name.Contains("PhysBoneEditor") &&
                        typeof(Editor).IsAssignableFrom(t));

                    foreach (var candidateType in types)
                    {
                        // Check if this editor is for VRCPhysBone
                        var customEditorAttrs = candidateType.GetCustomAttributes(typeof(CustomEditor), true);
                        foreach (CustomEditor attr in customEditorAttrs)
                        {
                            // Use reflection to access the private m_InspectedType field
                            var inspectedTypeField = typeof(CustomEditor).GetField("m_InspectedType",
                                BindingFlags.NonPublic | BindingFlags.Instance);

                            if (inspectedTypeField != null)
                            {
                                var inspectedType = inspectedTypeField.GetValue(attr) as System.Type;
                                if (inspectedType == typeof(VRCPhysBone))
                                {
                                    //Debug.Log($"<color=#00FF00>[SDK at Home]</color> Found PhysBone editor: {candidateType.FullName}");
                                    return candidateType;
                                }
                            }
                        }
                    }
                }
                catch (System.Exception)
                {
                    // Skip assemblies that can't be reflected upon
                    continue;
                }
            }

            return null;
        }

        [HarmonyPrefix]
        static void Prefix(Editor __instance)
        {
            // Verify this is actually a PhysBone editor instance
            if (__instance.GetType() != physBoneEditorType)
                return;

            if (Application.isPlaying)
            {
                VRCPhysBone physBone = __instance.target as VRCPhysBone;
                if (physBone != null)
                {
                    // Start tracking this PhysBone for undo detection
                    PhysBoneRestartManager.StartTracking(physBone);

                    // Start change check for direct inspector changes
                    EditorGUI.BeginChangeCheck();
                }
            }
        }

        [HarmonyPostfix]
        static void Postfix(Editor __instance)
        {
            // Verify this is actually a PhysBone editor instance
            if (__instance.GetType() != physBoneEditorType)
                return;

            // Only proceed if we're in play mode
            if (!Application.isPlaying)
                return;

            VRCPhysBone physBone = __instance.target as VRCPhysBone;
            if (physBone == null) return;

            // Check for direct inspector changes
            bool directChange = EditorGUI.EndChangeCheck();

            // Check for undo/redo changes by comparing current state with tracked state
            bool undoChange = PhysBoneRestartManager.CheckForUndoChange(physBone);

            if (directChange || undoChange)
            {
                if (physBone.enabled)
                {
                    PhysBoneRestartManager.ScheduleRestart(physBone);
                }
            }
        }
    }

    // Utility class to manage multiple PhysBone restarts
    public static class PhysBoneRestartManager
    {
        private static System.Collections.Generic.HashSet<VRCPhysBone> pendingRestarts =
            new System.Collections.Generic.HashSet<VRCPhysBone>();
        private static System.Collections.Generic.Dictionary<int, string> trackedPhysBones =
            new System.Collections.Generic.Dictionary<int, string>();

        static PhysBoneRestartManager()
        {
            // Subscribe to undo/redo events
            Undo.undoRedoPerformed += OnUndoRedoPerformed;

            // Subscribe to hierarchy changes to detect new/deleted PhysBones
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
        }

        public static void ScheduleRestart(VRCPhysBone physBone)
        {
            if (physBone != null && physBone.enabled && !pendingRestarts.Contains(physBone))
            {
                // Disable immediately
                physBone.enabled = false;
                pendingRestarts.Add(physBone);

                // Schedule re-enable for next frame
                EditorApplication.delayCall += () => RestartPendingPhysBones();
            }
        }

        public static void StartTracking(VRCPhysBone physBone)
        {
            if (physBone != null && Application.isPlaying)
            {
                // Use instance ID as key to avoid issues with destroyed objects
                int instanceId = physBone.GetInstanceID();
                string currentState = SerializePhysBoneState(physBone);
                trackedPhysBones[instanceId] = currentState;
            }
        }

        public static bool CheckForUndoChange(VRCPhysBone physBone)
        {
            if (physBone == null || !Application.isPlaying) return false;

            int instanceId = physBone.GetInstanceID();
            string currentState = SerializePhysBoneState(physBone);

            if (trackedPhysBones.TryGetValue(instanceId, out string oldState))
            {
                if (oldState != currentState)
                {
                    // Update the tracked state
                    trackedPhysBones[instanceId] = currentState;
                    return true;
                }
            }
            else
            {
                // Start tracking if not already tracked
                trackedPhysBones[instanceId] = currentState;
            }

            return false;
        }

        private static void OnHierarchyChanged()
        {
            if (!Application.isPlaying) return;

            // Find all PhysBones in the scene (including newly added ones)
            var allPhysBones = Object.FindObjectsOfType<VRCPhysBone>();
            var currentInstanceIds = new System.Collections.Generic.HashSet<int>();

            // Track new PhysBones and update existing ones
            foreach (var physBone in allPhysBones)
            {
                if (physBone != null)
                {
                    int instanceId = physBone.GetInstanceID();
                    currentInstanceIds.Add(instanceId);

                    // If this is a new PhysBone, start tracking it
                    if (!trackedPhysBones.ContainsKey(instanceId))
                    {
                        trackedPhysBones[instanceId] = SerializePhysBoneState(physBone);
                        //Debug.Log($"<color=#00FF00>[SDK at Home]</color> Started tracking new PhysBone: {physBone.name}");
                    }
                }
            }

            // Clean up tracking for deleted PhysBones
            var keysToRemove = new System.Collections.Generic.List<int>();
            foreach (var instanceId in trackedPhysBones.Keys)
            {
                if (!currentInstanceIds.Contains(instanceId))
                {
                    keysToRemove.Add(instanceId);
                }
            }

            foreach (var key in keysToRemove)
            {
                trackedPhysBones.Remove(key);
            }
        }

        private static void OnUndoRedoPerformed()
        {
            if (!Application.isPlaying) return;

            // Get all current PhysBones (some might have been deleted/added via undo)
            var allPhysBones = Object.FindObjectsOfType<VRCPhysBone>();
            var toRestart = new System.Collections.Generic.List<VRCPhysBone>();

            foreach (var physBone in allPhysBones)
            {
                if (physBone == null) continue;

                int instanceId = physBone.GetInstanceID();
                string currentState = SerializePhysBoneState(physBone);

                // Check if this PhysBone's state changed
                if (trackedPhysBones.TryGetValue(instanceId, out string oldState))
                {
                    if (oldState != currentState)
                    {
                        toRestart.Add(physBone);
                        trackedPhysBones[instanceId] = currentState;
                    }
                }
                else
                {
                    // This is a newly created PhysBone (via undo of deletion)
                    trackedPhysBones[instanceId] = currentState;
                    toRestart.Add(physBone); // Restart newly restored PhysBones
                    //Debug.Log($"<color=#00FF00>[SDK at Home]</color> Detected restored PhysBone via undo: {physBone.name}");
                }
            }

            // Restart changed PhysBones
            foreach (var physBone in toRestart)
            {
                if (physBone.enabled)
                {
                    ScheduleRestart(physBone);
                }
            }

            // Clean up any instance IDs that no longer exist (deleted via undo)
            var currentInstanceIds = new System.Collections.Generic.HashSet<int>(
                allPhysBones.Where(pb => pb != null).Select(pb => pb.GetInstanceID()));

            var keysToRemove = trackedPhysBones.Keys.Where(id => !currentInstanceIds.Contains(id)).ToArray();
            foreach (var key in keysToRemove)
            {
                trackedPhysBones.Remove(key);
            }
        }

        private static string SerializePhysBoneState(VRCPhysBone physBone)
        {
            if (physBone == null) return "";

            try
            {
                // Create a simple hash of key properties that affect physics behavior
                var state = $"{physBone.pull}|{physBone.pullCurve}|{physBone.spring}|{physBone.springCurve}|" +
                           $"{physBone.stiffness}|{physBone.stiffnessCurve}|{physBone.gravity}|{physBone.gravityFalloff}|" +
                           $"{physBone.immobile}|{physBone.immobileType}|{physBone.immobileCurve}|" +
                           $"{physBone.colliders?.Count ?? 0}|{physBone.radius}|{physBone.radiusCurve}|" +
                           $"{physBone.allowCollision}|{physBone.allowGrabbing}|{physBone.allowPosing}|" +
                           $"{physBone.grabMovement}|{physBone.maxStretch}|{physBone.maxSquish}|" +
                           $"{physBone.stretchMotion}|{physBone.maxAngleX}|{physBone.maxAngleZ}|" +
                           $"{physBone.limitType}|{physBone.rootTransform?.GetInstanceID() ?? 0}|" +
                           $"{physBone.ignoreTransforms?.Count ?? 0}|{physBone.endpointPosition}|{physBone.multiChildType}";

                return state;
            }
            catch (System.Exception)
            {
                // If object is destroyed or inaccessible, return empty string
                return "";
            }
        }

        private static void RestartPendingPhysBones()
        {
            var toRestart = new System.Collections.Generic.List<VRCPhysBone>(pendingRestarts);
            pendingRestarts.Clear();

            foreach (var physBone in toRestart)
            {
                // Double-check the PhysBone still exists before trying to enable it
                if (physBone != null && physBone.gameObject != null)
                {
                    physBone.enabled = true;
                    //Debug.Log($"<color=#00FF00>[SDK at Home]</color> Restarted PhysBone: {physBone.name}");
                }
            }
        }
    }
}
#endif