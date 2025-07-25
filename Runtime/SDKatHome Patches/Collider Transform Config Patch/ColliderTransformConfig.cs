#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace SDKatHome.Patches
{
    [SDKPatch("Simple Collider Transform Fields",
              "Adds transform override fields for custom colliders",
              "Avatar Tools",
              usePrefix: true,
              usePostfix: true)]
    public class ColliderTransformConfig
    {
        private static MethodInfo cachedTargetMethod;

        // Persistent storage for custom transforms - using stable GameObject GUID instead of instanceId
        private static Dictionary<string, Dictionary<string, string>> customTransformGuids =
            new Dictionary<string, Dictionary<string, string>>();

        // Runtime cache for resolved transforms
        private static Dictionary<string, Dictionary<string, Transform>> customTransforms =
            new Dictionary<string, Dictionary<string, Transform>>();

        // Store the last known state of each collider to detect changes
        private static Dictionary<string, Dictionary<string, VRCAvatarDescriptor.ColliderConfig.State>> lastKnownStates =
            new Dictionary<string, Dictionary<string, VRCAvatarDescriptor.ColliderConfig.State>>();

        // Track when we're applying our own changes to prevent recursion
        private static bool isApplyingCustomChanges = false;

        // Cache loaded status to avoid redundant loading
        private static bool hasLoadedFromPrefs = false;

        public static string[] GetPreferenceKeys()
        {
            return new string[]
            {
                "ColliderTransformConfig_CustomTransforms"
            };
        }

        public static MethodBase TargetMethod()
        {
            if (cachedTargetMethod != null)
                return cachedTargetMethod;

            try
            {
                // Find all assemblies that might contain VRC components
                var vrcAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => a.GetName().Name.Contains("VRC") ||
                               a.GetName().Name.Contains("VRCSDK") ||
                               a.GetName().Name.Contains("SDK"))
                    .ToArray();

                // Search in VRC assemblies first
                foreach (var assembly in vrcAssemblies)
                {
                    try
                    {
                        var editorType = assembly.GetTypes().FirstOrDefault(t =>
                            t.Name == "VRCAvatarDescriptorEditor" ||
                            t.Name == "AvatarDescriptorEditor");

                        if (editorType != null)
                        {
                            cachedTargetMethod = editorType.GetMethod("OnInspectorGUI",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                            if (cachedTargetMethod != null)
                            {
                                return cachedTargetMethod;
                            }
                        }
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                }

                // Fallback: search all assemblies
                var allAssemblies = AppDomain.CurrentDomain.GetAssemblies();
                foreach (var assembly in allAssemblies)
                {
                    try
                    {
                        var types = assembly.GetTypes();
                        var editorType = types.FirstOrDefault(t =>
                            (t.Name.Contains("VRCAvatarDescriptorEditor") || t.Name.Contains("AvatarDescriptorEditor")) &&
                            t.BaseType == typeof(Editor));

                        if (editorType != null)
                        {
                            cachedTargetMethod = editorType.GetMethod("OnInspectorGUI",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                            if (cachedTargetMethod != null)
                            {
                                return cachedTargetMethod;
                            }
                        }
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                }

                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static bool Prefix(Editor __instance)
        {
            if (__instance?.target is VRCAvatarDescriptor descriptor)
            {
                // Load from persistent storage on first access
                if (!hasLoadedFromPrefs)
                {
                    LoadFromEditorPrefs();
                    hasLoadedFromPrefs = true;
                }

                // Check for state changes and handle defaults
                HandleStateChanges(descriptor);

                // Restore our custom transforms before VRC draws the inspector
                RestoreCustomTransforms(descriptor);
            }
            return true;
        }

        public static void Postfix(Editor __instance)
        {
            if (__instance?.target is VRCAvatarDescriptor descriptor)
            {
                try
                {
                    // Apply our custom transforms after VRC draws
                    ApplyCustomTransforms(descriptor);

                    // Draw our UI
                    DrawTransformOverrideSection(descriptor);
                }
                catch (Exception) { }
            }
        }

        #region Persistence Methods

        private static string GetStableDescriptorId(VRCAvatarDescriptor descriptor)
        {
            // Use GlobalObjectId for stable identification across sessions
            var globalId = GlobalObjectId.GetGlobalObjectIdSlow(descriptor);
            return globalId.ToString();
        }

        private static string GetStableTransformId(Transform transform)
        {
            // Use GlobalObjectId for stable transform identification
            var globalId = GlobalObjectId.GetGlobalObjectIdSlow(transform);
            return globalId.ToString();
        }

        private static Transform FindTransformByStableId(string stableId)
        {
            try
            {
                if (string.IsNullOrEmpty(stableId)) return null;

                // Try to parse the GlobalObjectId
                if (GlobalObjectId.TryParse(stableId, out GlobalObjectId globalId))
                {
                    var obj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(globalId);
                    if (obj is GameObject gameObject)
                    {
                        return gameObject.transform;
                    }
                    else if (obj is Transform transform)
                    {
                        return transform;
                    }
                }

                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void LoadFromEditorPrefs()
        {
            try
            {
                string savedData = EditorPrefs.GetString("ColliderTransformConfig_CustomTransforms", "");
                if (!string.IsNullOrEmpty(savedData))
                {
                    var lines = savedData.Split('\n');
                    foreach (var line in lines)
                    {
                        if (string.IsNullOrEmpty(line)) continue;

                        var parts = line.Split('|');
                        if (parts.Length != 3) continue;

                        string descriptorId = parts[0];
                        string fieldName = parts[1];
                        string transformId = parts[2];

                        // Store the stable IDs
                        if (!customTransformGuids.ContainsKey(descriptorId))
                            customTransformGuids[descriptorId] = new Dictionary<string, string>();

                        customTransformGuids[descriptorId][fieldName] = transformId;
                    }
                }
            }
            catch (Exception) { }
        }

        private static void SaveToEditorPrefs()
        {
            try
            {
                var lines = new List<string>();

                foreach (var descriptorKvp in customTransformGuids)
                {
                    string descriptorId = descriptorKvp.Key;
                    foreach (var fieldKvp in descriptorKvp.Value)
                    {
                        string fieldName = fieldKvp.Key;
                        string transformId = fieldKvp.Value;

                        if (!string.IsNullOrEmpty(transformId))
                        {
                            lines.Add($"{descriptorId}|{fieldName}|{transformId}");
                        }
                    }
                }

                string savedData = string.Join("\n", lines);
                EditorPrefs.SetString("ColliderTransformConfig_CustomTransforms", savedData);
            }
            catch (Exception) { }
        }

        private static void ResolveTransformsForDescriptor(VRCAvatarDescriptor descriptor)
        {
            string descriptorId = GetStableDescriptorId(descriptor);

            if (!customTransformGuids.ContainsKey(descriptorId)) return;

            if (!customTransforms.ContainsKey(descriptorId))
                customTransforms[descriptorId] = new Dictionary<string, Transform>();

            var guidDict = customTransformGuids[descriptorId];
            var transformDict = customTransforms[descriptorId];

            foreach (var kvp in guidDict)
            {
                string fieldName = kvp.Key;
                string transformId = kvp.Value;

                // Only resolve if we don't already have it cached or if it's null
                if (!transformDict.ContainsKey(fieldName) || transformDict[fieldName] == null)
                {
                    var transform = FindTransformByStableId(transformId);
                    transformDict[fieldName] = transform;
                }
            }
        }

        #endregion

        private static void HandleStateChanges(VRCAvatarDescriptor descriptor)
        {
            if (isApplyingCustomChanges) return;

            string descriptorId = GetStableDescriptorId(descriptor);

            // Resolve transforms for this descriptor
            ResolveTransformsForDescriptor(descriptor);

            // Initialize tracking if needed
            if (!lastKnownStates.ContainsKey(descriptorId))
                lastKnownStates[descriptorId] = new Dictionary<string, VRCAvatarDescriptor.ColliderConfig.State>();

            var currentStates = lastKnownStates[descriptorId];
            var allFields = GetColliderFieldsInfo();

            foreach (var (fieldName, displayName) in allFields)
            {
                var config = GetColliderConfig(descriptor, fieldName);
                if (!config.HasValue) continue;

                var currentState = config.Value.state;
                var hadPreviousState = currentStates.ContainsKey(fieldName);
                var previousState = hadPreviousState ? currentStates[fieldName] : VRCAvatarDescriptor.ColliderConfig.State.Automatic;

                // Update our tracking
                currentStates[fieldName] = currentState;

                // Handle state transitions
                if (hadPreviousState && previousState != currentState)
                {
                    HandleStateTransition(descriptor, fieldName, displayName, previousState, currentState);
                }
                else if (!hadPreviousState && currentState == VRCAvatarDescriptor.ColliderConfig.State.Custom)
                {
                    // First time seeing this as custom, set up default
                    SetupCustomDefault(descriptor, fieldName, displayName);
                }
            }
        }

        private static void HandleStateTransition(VRCAvatarDescriptor descriptor, string fieldName, string displayName,
            VRCAvatarDescriptor.ColliderConfig.State fromState, VRCAvatarDescriptor.ColliderConfig.State toState)
        {
            try
            {
                isApplyingCustomChanges = true;

                if (toState == VRCAvatarDescriptor.ColliderConfig.State.Custom)
                {
                    // Switching TO Custom - populate with SDK's calculated value
                    var defaultTransform = GetDefaultTransformForCollider(descriptor, fieldName);
                    if (defaultTransform != null)
                    {
                        SetCustomTransform(descriptor, fieldName, defaultTransform);

                        var config = GetColliderConfig(descriptor, fieldName);
                        if (config.HasValue)
                        {
                            var updatedConfig = config.Value;
                            updatedConfig.transform = defaultTransform;
                            SetColliderConfig(descriptor, fieldName, updatedConfig);
                        }
                    }
                }
                else if (fromState == VRCAvatarDescriptor.ColliderConfig.State.Custom)
                {
                    // Switching FROM Custom - restore original/clear our custom value
                    string descriptorId = GetStableDescriptorId(descriptor);
                    if (customTransformGuids.ContainsKey(descriptorId))
                    {
                        customTransformGuids[descriptorId].Remove(fieldName);
                    }
                    if (customTransforms.ContainsKey(descriptorId))
                    {
                        customTransforms[descriptorId].Remove(fieldName);
                    }
                    SaveToEditorPrefs(); // Save immediately when removing
                }
            }
            finally
            {
                isApplyingCustomChanges = false;
            }
        }

        private static void SetupCustomDefault(VRCAvatarDescriptor descriptor, string fieldName, string displayName)
        {
            // First time seeing this collider as custom, check if it needs a default
            string descriptorId = GetStableDescriptorId(descriptor);

            // If we don't have a stored custom transform, set up the default
            if (!customTransforms.ContainsKey(descriptorId) ||
                !customTransforms[descriptorId].ContainsKey(fieldName) ||
                customTransforms[descriptorId][fieldName] == null)
            {
                var config = GetColliderConfig(descriptor, fieldName);
                if (config.HasValue && config.Value.transform == null)
                {
                    // No transform set, use SDK's calculated default
                    var defaultTransform = GetDefaultTransformForCollider(descriptor, fieldName);
                    if (defaultTransform != null)
                    {
                        SetCustomTransform(descriptor, fieldName, defaultTransform);

                        try
                        {
                            isApplyingCustomChanges = true;
                            var updatedConfig = config.Value;
                            updatedConfig.transform = defaultTransform;
                            SetColliderConfig(descriptor, fieldName, updatedConfig);
                        }
                        finally
                        {
                            isApplyingCustomChanges = false;
                        }
                    }
                }
            }
        }

        private static Transform GetDefaultTransformForCollider(VRCAvatarDescriptor descriptor, string fieldName)
        {
            // Use VRC's built-in calculation methods to get the appropriate default transform
            var animator = descriptor.GetComponent<Animator>();
            if (animator == null) return null;

            try
            {
                switch (fieldName)
                {
                    case "collider_head":
                        var headConfig = VRCAvatarDescriptor.CalcHeadCollider(animator, descriptor.ViewPosition);
                        return headConfig.transform;

                    case "collider_torso":
                        var torsoConfig = VRCAvatarDescriptor.CalcTorsoCollider(animator);
                        return torsoConfig.transform;

                    case "collider_handL":
                        var handLConfig = VRCAvatarDescriptor.CalcPalmCollider(animator, true);
                        return handLConfig.transform;

                    case "collider_handR":
                        var handRConfig = VRCAvatarDescriptor.CalcPalmCollider(animator, false);
                        return handRConfig.transform;

                    case "collider_footL":
                        var footLConfig = VRCAvatarDescriptor.CalcFootCollider(animator, true);
                        return footLConfig.transform;

                    case "collider_footR":
                        var footRConfig = VRCAvatarDescriptor.CalcFootCollider(animator, false);
                        return footRConfig.transform;

                    case "collider_fingerIndexL":
                        var fingerIndexLConfig = VRCAvatarDescriptor.CalcFingerCollider(animator, 0, true);
                        return fingerIndexLConfig.transform;

                    case "collider_fingerIndexR":
                        var fingerIndexRConfig = VRCAvatarDescriptor.CalcFingerCollider(animator, 0, false);
                        return fingerIndexRConfig.transform;

                    case "collider_fingerMiddleL":
                        var fingerMiddleLConfig = VRCAvatarDescriptor.CalcFingerCollider(animator, 1, true);
                        return fingerMiddleLConfig.transform;

                    case "collider_fingerMiddleR":
                        var fingerMiddleRConfig = VRCAvatarDescriptor.CalcFingerCollider(animator, 1, false);
                        return fingerMiddleRConfig.transform;

                    case "collider_fingerRingL":
                        var fingerRingLConfig = VRCAvatarDescriptor.CalcFingerCollider(animator, 2, true);
                        return fingerRingLConfig.transform;

                    case "collider_fingerRingR":
                        var fingerRingRConfig = VRCAvatarDescriptor.CalcFingerCollider(animator, 2, false);
                        return fingerRingRConfig.transform;

                    case "collider_fingerLittleL":
                        var fingerLittleLConfig = VRCAvatarDescriptor.CalcFingerCollider(animator, 3, true);
                        return fingerLittleLConfig.transform;

                    case "collider_fingerLittleR":
                        var fingerLittleRConfig = VRCAvatarDescriptor.CalcFingerCollider(animator, 3, false);
                        return fingerLittleRConfig.transform;

                    default:
                        return null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void RestoreCustomTransforms(VRCAvatarDescriptor descriptor)
        {
            if (isApplyingCustomChanges) return;

            string descriptorId = GetStableDescriptorId(descriptor);
            if (!customTransforms.ContainsKey(descriptorId)) return;

            var transforms = customTransforms[descriptorId];

            try
            {
                isApplyingCustomChanges = true;

                foreach (var kvp in transforms)
                {
                    string fieldName = kvp.Key;
                    Transform customTransform = kvp.Value;

                    if (customTransform == null) continue;

                    // Get the current config
                    var config = GetColliderConfig(descriptor, fieldName);
                    if (config.HasValue && config.Value.state == VRCAvatarDescriptor.ColliderConfig.State.Custom)
                    {
                        // Only restore if it's different from what we want
                        if (config.Value.transform != customTransform)
                        {
                            var updatedConfig = config.Value;
                            updatedConfig.transform = customTransform;
                            SetColliderConfig(descriptor, fieldName, updatedConfig);
                        }
                    }
                }
            }
            finally
            {
                isApplyingCustomChanges = false;
            }
        }

        private static void ApplyCustomTransforms(VRCAvatarDescriptor descriptor)
        {
            if (isApplyingCustomChanges) return;

            string descriptorId = GetStableDescriptorId(descriptor);
            if (!customTransforms.ContainsKey(descriptorId)) return;

            // Force apply our transforms again in case VRC overwrote them
            RestoreCustomTransforms(descriptor);
        }

        private static void DrawTransformOverrideSection(VRCAvatarDescriptor descriptor)
        {
            var customColliders = GetCustomColliderFields(descriptor);
            if (customColliders.Count == 0) return;

            // Create a visually distinct section
            EditorGUILayout.Space(8);

            // Draw separator line
            var rect = EditorGUILayout.GetControlRect(false, 2);
            EditorGUI.DrawRect(rect, new Color(0.3f, 0.7f, 0.3f, 0.8f));

            EditorGUILayout.Space(5);

            // Header with icon and styling
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(10);
                var headerStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 12,
                    normal = { textColor = new Color(0.3f, 0.9f, 0.3f) }
                };
                EditorGUILayout.LabelField("Transform Overrides", headerStyle);
            }

            EditorGUILayout.Space(3);

            // Draw each custom collider's transform field
            foreach (var colliderInfo in customColliders)
            {
                DrawTransformField(colliderInfo.fieldName, colliderInfo.displayName, descriptor);
            }

            EditorGUILayout.Space(8);
        }

        private static void DrawTransformField(string fieldName, string displayName, VRCAvatarDescriptor descriptor)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                // Indentation to match Unity's style
                GUILayout.Space(20);

                // Create a styled label
                var labelStyle = new GUIStyle(EditorStyles.label)
                {
                    normal = { textColor = new Color(0.8f, 0.9f, 1f) },
                    fontStyle = FontStyle.Normal
                };

                EditorGUILayout.LabelField($"{displayName}", labelStyle, GUILayout.Width(120));

                // Get our stored custom transform
                string descriptorId = GetStableDescriptorId(descriptor);
                Transform currentCustomTransform = null;

                if (customTransforms.ContainsKey(descriptorId) && customTransforms[descriptorId].ContainsKey(fieldName))
                {
                    currentCustomTransform = customTransforms[descriptorId][fieldName];
                }

                // Transform object field
                EditorGUI.BeginChangeCheck();

                var newTransform = EditorGUILayout.ObjectField(
                    currentCustomTransform,
                    typeof(Transform),
                    true,
                    GUILayout.ExpandWidth(true)) as Transform;

                if (EditorGUI.EndChangeCheck())
                {
                    // If setting to null, restore default value
                    if (newTransform == null)
                    {
                        newTransform = GetDefaultTransformForCollider(descriptor, fieldName);
                    }

                    // Store the custom transform
                    SetCustomTransform(descriptor, fieldName, newTransform);

                    // Apply it immediately to the actual config
                    var config = GetColliderConfig(descriptor, fieldName);
                    if (config.HasValue)
                    {
                        var updatedConfig = config.Value;
                        updatedConfig.transform = newTransform;
                        SetColliderConfig(descriptor, fieldName, updatedConfig);
                        EditorUtility.SetDirty(descriptor);
                    }
                }

                if (GUILayout.Button("Reset", GUILayout.Width(60), GUILayout.Height(18)))
                {
                    // Get the default transform for this collider
                    var defaultTransform = GetDefaultTransformForCollider(descriptor, fieldName);

                    // Update our custom transform storage
                    SetCustomTransform(descriptor, fieldName, defaultTransform);

                    // Apply it to the actual config
                    var config = GetColliderConfig(descriptor, fieldName);
                    if (config.HasValue)
                    {
                        var updatedConfig = config.Value;
                        updatedConfig.transform = defaultTransform;
                        SetColliderConfig(descriptor, fieldName, updatedConfig);
                        EditorUtility.SetDirty(descriptor);
                    }
                }

                // Add a small help icon if transform is null
                if (currentCustomTransform == null)
                {
                    var warningStyle = new GUIStyle(EditorStyles.label)
                    {
                        normal = { textColor = Color.yellow }
                    };

                    EditorGUILayout.LabelField("⚠", warningStyle, GUILayout.Width(20));
                }
                else
                {
                    // Add some spacing to keep layout consistent when no warning icon
                    GUILayout.Space(20);
                }
            }
        }

        private static void SetCustomTransform(VRCAvatarDescriptor descriptor, string fieldName, Transform transform)
        {
            string descriptorId = GetStableDescriptorId(descriptor);

            // Store both the GUID and the runtime reference
            if (!customTransformGuids.ContainsKey(descriptorId))
                customTransformGuids[descriptorId] = new Dictionary<string, string>();

            if (!customTransforms.ContainsKey(descriptorId))
                customTransforms[descriptorId] = new Dictionary<string, Transform>();

            if (transform != null)
            {
                string transformId = GetStableTransformId(transform);
                customTransformGuids[descriptorId][fieldName] = transformId;
                customTransforms[descriptorId][fieldName] = transform;
            }
            else
            {
                customTransformGuids[descriptorId].Remove(fieldName);
                customTransforms[descriptorId].Remove(fieldName);
            }

            // Save to persistent storage immediately
            SaveToEditorPrefs();
        }

        private static List<(string fieldName, string displayName)> GetCustomColliderFields(VRCAvatarDescriptor descriptor)
        {
            var customColliders = new List<(string, string)>();
            var allFields = GetColliderFieldsInfo();

            foreach (var (fieldName, displayName) in allFields)
            {
                var config = GetColliderConfig(descriptor, fieldName);
                if (config.HasValue && config.Value.state == VRCAvatarDescriptor.ColliderConfig.State.Custom)
                {
                    customColliders.Add((fieldName, displayName));
                }
            }

            return customColliders;
        }

        private static VRCAvatarDescriptor.ColliderConfig? GetColliderConfig(VRCAvatarDescriptor descriptor, string fieldName)
        {
            try
            {
                var field = typeof(VRCAvatarDescriptor).GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
                if (field != null)
                {
                    return (VRCAvatarDescriptor.ColliderConfig)field.GetValue(descriptor);
                }
            }
            catch (Exception) { }

            return null;
        }

        private static void SetColliderConfig(VRCAvatarDescriptor descriptor, string fieldName, VRCAvatarDescriptor.ColliderConfig config)
        {
            try
            {
                var field = typeof(VRCAvatarDescriptor).GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
                if (field != null)
                {
                    field.SetValue(descriptor, config);
                }
            }
            catch (Exception) { }
        }

        private static (string fieldName, string displayName)[] GetColliderFieldsInfo()
        {
            return new[]
            {
                ("collider_head", "Head"),
                ("collider_torso", "Torso"),
                ("collider_handL", "Left Hand"),
                ("collider_handR", "Right Hand"),
                ("collider_footL", "Left Foot"),
                ("collider_footR", "Right Foot"),
                ("collider_fingerIndexL", "Left Index"),
                ("collider_fingerIndexR", "Right Index"),
                ("collider_fingerMiddleL", "Left Middle"),
                ("collider_fingerMiddleR", "Right Middle"),
                ("collider_fingerRingL", "Left Ring"),
                ("collider_fingerRingR", "Right Ring"),
                ("collider_fingerLittleL", "Left Little"),
                ("collider_fingerLittleR", "Right Little")
            };
        }

        // Clean up methods - but don't clear the persistent storage
        [UnityEditor.Callbacks.DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            // Reset runtime cache but keep persistent data
            customTransforms.Clear();
            lastKnownStates.Clear();
            hasLoadedFromPrefs = false;
        }

        [RuntimeInitializeOnLoadMethod]
        private static void OnEnterPlayMode()
        {
            // Reset runtime cache but keep persistent data
            customTransforms.Clear();
            lastKnownStates.Clear();
            hasLoadedFromPrefs = false;
        }
    }
}
#endif