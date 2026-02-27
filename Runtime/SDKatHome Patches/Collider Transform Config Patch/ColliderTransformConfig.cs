#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using static VRC.SDK3.Avatars.Components.VRCAvatarDescriptor;

namespace SDKatHome.Patches
{
    public class ColliderTransformConfig : SDKPatchBase
    {
        public override string PatchName => "Simple Collider Transform Fields";
        public override string Description => "Adds transform override fields for custom colliders";
        public override string Category => "Avatar Tools";
        public override bool UsePrefix => true;
        public override bool UsePostfix => true;
        public override bool EnabledByDefault => false;


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
        private static readonly HashSet<int> _loadedEditorInstances = new HashSet<int>();

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
                int editorId = __instance.GetInstanceID();

                if (!_loadedEditorInstances.Contains(editorId))
                {
                    _loadedEditorInstances.Add(editorId);

                    // Reload fresh data whenever the inspector is shown
                    LoadFromJson();
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

        // Clean up methods - but don't clear the persistent storage
        [UnityEditor.Callbacks.DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            // Reset runtime cache but keep persistent data
            customTransforms.Clear();
            lastKnownStates.Clear();
            _loadedEditorInstances.Clear();
        }

        [RuntimeInitializeOnLoadMethod]
        private static void OnEnterPlayMode()
        {
            // Reset runtime cache but keep persistent data
            customTransforms.Clear();
            lastKnownStates.Clear();
            _loadedEditorInstances.Clear();
        }

        #region Persistence Methods

        private static string GetStableDescriptorId(VRCAvatarDescriptor descriptor)
        {
            if (descriptor == null) return null;

            var idComp = descriptor.GetComponent<CustomColliderGuid>();
            return (idComp != null && !string.IsNullOrEmpty(idComp.avatarId))
                ? idComp.avatarId
                : null;
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

        private static void ResolveTransformsForDescriptor(VRCAvatarDescriptor descriptor)
        {
            string descriptorId = GetStableDescriptorId(descriptor);
            if (descriptorId == null) return;

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

        #endregion Persistence Methods

        #region Save System

        private static void LoadFromJson()
        {
            try
            {
                customTransformGuids.Clear();

                if (!System.IO.File.Exists(DataFilePath))
                    return;

                string json = System.IO.File.ReadAllText(DataFilePath);
                var db = JsonUtility.FromJson<ColliderTransformDatabase>(json);
                if (db?.avatars == null) return;

                foreach (var avatarEntry in db.avatars)
                {
                    if (string.IsNullOrEmpty(avatarEntry?.descriptorId))
                        continue;

                    if (!customTransformGuids.ContainsKey(avatarEntry.descriptorId))
                        customTransformGuids[avatarEntry.descriptorId] = new Dictionary<string, string>();

                    var dict = customTransformGuids[avatarEntry.descriptorId];

                    if (avatarEntry.fields == null) continue;
                    foreach (var f in avatarEntry.fields)
                    {
                        if (string.IsNullOrEmpty(f?.fieldName)) continue;
                        dict[f.fieldName] = f.transformId; // transformId may be null/empty -> okay
                    }
                }
            }
            catch (Exception) { }
        }

        private static void SaveToJson(VRCAvatarDescriptor descriptorForName = null)
        {
            try
            {
                if (DetectEditorContext() != "EditMode") return;

                var oldDb = LoadDbOrEmpty();

                // ── Preserve avatar names ──
                var oldNames = new Dictionary<string, string>();
                var oldTransformNames = new Dictionary<string, Dictionary<string, string>>();
                var oldSaveTime = new Dictionary<string, string>();
                var oldSaveContext = new Dictionary<string, string>();

                if (oldDb.avatars != null)
                {
                    foreach (var avatar in oldDb.avatars)
                    {
                        if (string.IsNullOrEmpty(avatar?.descriptorId))
                            continue;

                        if (!string.IsNullOrEmpty(avatar.avatarName))
                            oldNames[avatar.descriptorId] = avatar.avatarName;

                        if (!string.IsNullOrEmpty(avatar.lastSavedTime))
                            oldSaveTime[avatar.descriptorId] = avatar.lastSavedTime;

                        if (!string.IsNullOrEmpty(avatar.lastSaveContext))
                            oldSaveContext[avatar.descriptorId] = avatar.lastSaveContext;

                        if (avatar.fields != null)
                        {
                            var map = new Dictionary<string, string>();
                            foreach (var field in avatar.fields)
                            {
                                if (!string.IsNullOrEmpty(field?.fieldName) &&
                                    !string.IsNullOrEmpty(field.transformName))
                                {
                                    map[field.fieldName] = field.transformName;
                                }
                            }

                            if (map.Count > 0)
                                oldTransformNames[avatar.descriptorId] = map;
                        }
                    }
                }

                string currentDescriptorId = null;
                if (descriptorForName != null)
                    currentDescriptorId = GetStableDescriptorId(descriptorForName);

                var db = new ColliderTransformDatabase();

                foreach (var descriptorKvp in customTransformGuids)
                {
                    var descriptorId = descriptorKvp.Key;

                    // ── Decide avatar name ──
                    string avatarName = null;

                    if (descriptorForName != null &&
                        currentDescriptorId == descriptorId)
                    {
                        avatarName = descriptorForName.gameObject.name;
                    }
                    else if (oldNames.TryGetValue(descriptorId, out var oldName))
                    {
                        avatarName = oldName;
                    }

                    string lastSavedLocal = null;
                    string lastSaveContext = null;

                    bool isCurrentAvatar = (currentDescriptorId != null && currentDescriptorId == descriptorId);

                    if (!isCurrentAvatar)
                    {
                        // keep old values if present
                        oldSaveTime.TryGetValue(descriptorId, out lastSavedLocal);
                        oldSaveContext.TryGetValue(descriptorId, out lastSaveContext);
                    }

                    // If current avatar OR no previous values exist, set them now
                    if (string.IsNullOrEmpty(lastSavedLocal) || string.IsNullOrEmpty(lastSaveContext) || isCurrentAvatar)
                    {
                        lastSavedLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        lastSaveContext = DetectEditorContext();
                    }

                    var avatarEntry = new AvatarColliderTransformEntry
                    {
                        descriptorId = descriptorId,
                        avatarName = avatarName,
                        lastSavedTime = lastSavedLocal,
                        lastSaveContext = lastSaveContext
                    };

                    // ── Write field entries ──
                    foreach (var fieldKvp in descriptorKvp.Value)
                    {
                        var transformId = fieldKvp.Value;
                        if (string.IsNullOrEmpty(transformId))
                            continue;

                        string resolvedName = ResolveTransformName(transformId);
                        string finalName = resolvedName;

                        if (string.IsNullOrEmpty(resolvedName) &&
                            oldTransformNames.TryGetValue(descriptorId, out var oldFieldMap) &&
                            oldFieldMap.TryGetValue(fieldKvp.Key, out var oldFieldName))
                        {
                            finalName = oldFieldName.ToString();
                        }


                        avatarEntry.fields.Add(new ColliderTransformFieldEntry
                        {
                            fieldName = fieldKvp.Key,
                            transformId = transformId,
                            transformName = finalName,
                        });
                    }

                    if (avatarEntry.fields.Count > 0)
                        db.avatars.Add(avatarEntry);
                }

                var dir = System.IO.Path.GetDirectoryName(DataFilePath);
                if (!System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);

                System.IO.File.WriteAllText(
                    DataFilePath,
                    JsonUtility.ToJson(db, true)
                );
            }
            catch { }
        }

        private static ColliderTransformDatabase LoadDbOrEmpty()
        {
            try
            {
                if (!System.IO.File.Exists(DataFilePath))
                    return new ColliderTransformDatabase();

                var json = System.IO.File.ReadAllText(DataFilePath);
                var db = JsonUtility.FromJson<ColliderTransformDatabase>(json);
                return db ?? new ColliderTransformDatabase();
            }
            catch
            {
                return new ColliderTransformDatabase();
            }
        }

        private static string ResolveTransformName(string globalObjectIdString)
        {
            if (string.IsNullOrEmpty(globalObjectIdString))
                return null;

            if (!GlobalObjectId.TryParse(globalObjectIdString, out var gid))
                return null;

            var obj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid) as Transform;
            return obj != null ? obj.name : null;
        }

        private static bool IsTemporaryAvatar(VRCAvatarDescriptor descriptor)
        {
            if (descriptor == null) return true;

            var go = descriptor.gameObject;
            if (go == null) return true;

            return go.name.EndsWith("(Clone)", StringComparison.Ordinal);
        }

        private static void EnsureAvatarGuidIfNeeded(VRCAvatarDescriptor descriptor)
        {
            if (descriptor == null)
                return;

            // Never attach to temporary build clones
            if (IsTemporaryAvatar(descriptor))
                return;

            var go = descriptor.gameObject;
            var idComp = go.GetComponent<CustomColliderGuid>();

            if (idComp == null)
            {
                idComp = go.AddComponent<CustomColliderGuid>();
                EditorUtility.SetDirty(go);
            }

            if (string.IsNullOrEmpty(idComp.avatarId))
            {
                idComp.avatarId = Guid.NewGuid().ToString("N");
                EditorUtility.SetDirty(go);
            }
        }

        public static volatile bool _inVrcAvatarBuild;
        public static string DetectEditorContext()
        {
            // If your preprocess callback told us we are in a VRC avatar build
            if (_inVrcAvatarBuild)
                return "VrcAvatarBuild";

            // Unity player build (BuildPipeline.isBuildingPlayer exists in Editor)
            if (BuildPipeline.isBuildingPlayer)
                return "UnityBuild";

            // Play mode detection
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return "PlayMode";

            return "EditMode";
        }

        [Serializable]
        private class ColliderTransformFieldEntry
        {
            public string fieldName;
            public string transformId;
            public string transformName;
        }

        [Serializable]
        private class AvatarColliderTransformEntry
        {
            public string descriptorId;
            public string avatarName;
            public List<ColliderTransformFieldEntry> fields = new List<ColliderTransformFieldEntry>();
            public string lastSavedTime;      // YYYY-MM-dd HH:mm:ss
            public string lastSaveContext;   // "EditMode" / "PlayMode" / "UnityBuild" / "VrcAvatarBuild"
        }

        [Serializable]
        private class ColliderTransformDatabase
        {
            public int version = 1;
            public List<AvatarColliderTransformEntry> avatars = new List<AvatarColliderTransformEntry>();
        }

        private static string DataFilePath
        {
            get
            {
                var projectRoot = System.IO.Directory.GetParent(Application.dataPath).FullName;
                return System.IO.Path.Combine(projectRoot, "ProjectSettings", "SDKatHome", "ColliderTransformConfig.json");
            }
        }

        #endregion Save System

        private static void HandleStateChanges(VRCAvatarDescriptor descriptor)
        {
            if (isApplyingCustomChanges) return;
            if (descriptor == null) return;

            var allFields = GetColliderFieldsInfo();

            bool anyCustom = false;
            foreach (var (fieldName, _) in allFields)
            {
                var cfg = GetColliderConfig(descriptor, fieldName);
                if (!cfg.HasValue) continue;

                if (cfg.Value.state == VRCAvatarDescriptor.ColliderConfig.State.Custom)
                {
                    anyCustom = true;
                    break;
                }
            }

            if (!anyCustom)
                return;

            EnsureAvatarGuidIfNeeded(descriptor);

            string descriptorId = GetStableDescriptorId(descriptor);
            if (descriptorId == null) return;

            // Resolve transforms for this descriptor
            ResolveTransformsForDescriptor(descriptor);

            // Initialize tracking if needed
            if (!lastKnownStates.ContainsKey(descriptorId))
                lastKnownStates[descriptorId] = new Dictionary<string, VRCAvatarDescriptor.ColliderConfig.State>();

            var currentStates = lastKnownStates[descriptorId];

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
                    string descriptorId = GetStableDescriptorId(descriptor);
                    if (string.IsNullOrEmpty(descriptorId))return;

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

                    if (!IsTemporaryAvatar(descriptor))
                        SaveToJson(descriptor); // Save immediately when removing
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
            if (descriptor == null) return;

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
                        float prefabScale = Mathf.Max(
                            descriptor.transform.lossyScale.x,
                            descriptor.transform.lossyScale.y,
                            descriptor.transform.lossyScale.z
                        );

                        var headConfig = SafeCalcHeadCollider(animator, descriptor.ViewPosition, prefabScale);
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

        public static ColliderConfig SafeCalcHeadCollider(Animator animator, Vector3 localViewpoint, float prefabScale)
        {
            var type = typeof(VRCAvatarDescriptor);

            // Try new signature first (3 parameters)
            var method = type.GetMethod(
                "CalcHeadCollider",
                new[] { typeof(Animator), typeof(Vector3), typeof(float) }
            );

            if (method != null)
            {
                return (ColliderConfig)method.Invoke(
                    null,
                    new object[] { animator, localViewpoint, prefabScale }
                );
            }

            // Fallback to old signature (2 parameters)
            method = type.GetMethod(
                "CalcHeadCollider",
                new[] { typeof(Animator), typeof(Vector3) }
            );

            if (method != null)
            {
                return (ColliderConfig)method.Invoke(
                    null,
                    new object[] { animator, localViewpoint }
                );
            }

            Debug.LogError($"<color=#00FF00>[SDK at Home]</color> Couldn't find the CalcHeadCollider method in the SDK.");
            return new ColliderConfig();
        }

        private static void RestoreCustomTransforms(VRCAvatarDescriptor descriptor)
        {
            if (isApplyingCustomChanges) return;

            string descriptorId = GetStableDescriptorId(descriptor);
            if (descriptorId == null) return;

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

            EditorGUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(20);
                GUILayout.FlexibleSpace();

                GUI.enabled = customColliders.Count > 0;

                if (GUILayout.Button("Reset All", GUILayout.Width(80), GUILayout.Height(20)))
                {
                    if (EditorUtility.DisplayDialog(
                        "Reset all collider overrides?",
                        "This will reset all custom collider transforms back to their defaults.",
                        "Reset All",
                        "Cancel"
                        ))
                    {
                        ResetAllCustomCollidersToDefault(descriptor);
                    }
                }

                EditorGUILayout.LabelField("", GUILayout.Width(17));

                GUI.enabled = true;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(20);
                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Refresh", GUILayout.Width(80), GUILayout.Height(20)))
                {
                    ReloadData(descriptor);
                }

                EditorGUILayout.LabelField("", GUILayout.Width(17));
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
                    ResetCollidersToDefault(descriptor, fieldName);
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

        public static void SetCustomTransform(VRCAvatarDescriptor descriptor, string fieldName, Transform transform)
        {
            string descriptorId = GetStableDescriptorId(descriptor);
            if (string.IsNullOrEmpty(descriptorId)) return;

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
            if (!IsTemporaryAvatar(descriptor))
                SaveToJson(descriptor);
        }

        public static List<(string fieldName, string displayName)> GetCustomColliderFields(VRCAvatarDescriptor descriptor)
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

        public static void ResetCollidersToDefault(VRCAvatarDescriptor descriptor, string fieldName)
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

        public static void ResetAllCustomCollidersToDefault(VRCAvatarDescriptor descriptor)
        {
            if (descriptor == null) return;

            try
            {
                isApplyingCustomChanges = true;

                foreach (var (fieldName, _) in GetCustomColliderFields(descriptor))
                {
                    ResetCollidersToDefault(descriptor, fieldName);
                }
            }
            catch (Exception) { }
            finally
            {
                isApplyingCustomChanges = false;
            }
        }

        public static VRCAvatarDescriptor.ColliderConfig? GetColliderConfig(VRCAvatarDescriptor descriptor, string fieldName)
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

        public static void SetColliderConfig(VRCAvatarDescriptor descriptor, string fieldName, VRCAvatarDescriptor.ColliderConfig config)
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

        public static void ReloadData(VRCAvatarDescriptor descriptor)
        {
            // Reload fresh data
            LoadFromJson();

            // Check for state changes and handle defaults
            HandleStateChanges(descriptor);

            // Restore our custom transforms
            RestoreCustomTransforms(descriptor);

            // Apply our custom transforms
            ApplyCustomTransforms(descriptor);
        }

    }

    public class CustomTransformPreprocess : VRC.SDKBase.Editor.BuildPipeline.IVRCSDKPreprocessAvatarCallback
    {
        public int callbackOrder => 0;

        public bool OnPreprocessAvatar(GameObject avatar)
        {
            // If the patch or SDK at Home is disabled, we do nothing
            if (!SDKatHomePatcher.IsPatchActive(typeof(ColliderTransformConfig)) || !SDKatHomePatcher.IsSDKatHomeEnabled())
            {
                return true;
            }

            // We also don't do anything in play mode
            if (ColliderTransformConfig.DetectEditorContext() == "PlayMode")
            {
                Debug.LogWarning($"<color=#00FF00>[SDK at Home]</color> We are in Play Mode. Skipping preprocessing.");
                return true;
            }

            VRCAvatarDescriptor descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
            ColliderTransformConfig._inVrcAvatarBuild = true;

            if (descriptor == null)
            {
                Debug.LogError($"<color=#00FF00>[SDK at Home]</color> Avatar descriptor not found.");
                ColliderTransformConfig._inVrcAvatarBuild = false;
                return false;
            }

            var CustomFields = ColliderTransformConfig.GetCustomColliderFields(descriptor);

            if (CustomFields == null)
            {
                Debug.LogError($"<color=#00FF00>[SDK at Home]</color> Couldn't get Custom Colliders fields, but the avatar will upload. Custom Colliders will likely not work, even if they were set up.");
                return true;
            }

            if (CustomFields.Count == 0)
            {
                // No custom fields to restore
                return true;
            }

            var idComp = avatar.GetComponent<CustomColliderGuid>();
            if (idComp == null || string.IsNullOrEmpty(idComp.avatarId))
            {
                Debug.LogError("<color=#00FF00>[SDK at Home]</color> Your avatar has Custom Colliders, but doesn't have a Custom Collider Guid component. Set your colliders back to Automatic, and then to Custom to get one.");
                ColliderTransformConfig._inVrcAvatarBuild = false;
                return false;
            }

            string avatarId = idComp.avatarId;

            VRCAvatarDescriptor originalDescriptor = null;

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                    continue;

                foreach (GameObject go in scene.GetRootGameObjects())
                {
                    // Skip our own avatar
                    if (go == avatar)
                        continue;

                    // Skip any other clones
                    if (go.name.EndsWith("(Clone)", StringComparison.Ordinal))
                        continue;

                    var desc = go.GetComponent<VRCAvatarDescriptor>();
                    if (desc == null)
                        continue;

                    var id = go.GetComponent<CustomColliderGuid>();
                    if (id != null && id.avatarId == avatarId)
                    {
                        originalDescriptor = desc;
                        break;
                    }
                }

                if (originalDescriptor != null)
                    break;
            }

            if (originalDescriptor == null)
            {
                Debug.LogError($"<color=#00FF00>[SDK at Home]</color> Original avatar descriptor not found.");
                ColliderTransformConfig._inVrcAvatarBuild = false;
                ColliderTransformConfig.ReloadData(originalDescriptor);

                var sceneObjects = avatar.scene.GetRootGameObjects();

                StringBuilder sb = new StringBuilder();
                foreach (GameObject go in sceneObjects)
                {
                    sb.AppendLine(go.name);
                }
                Debug.LogError($"<color=#00FF00>[SDK at Home]</color> Object in the scene: {sb.ToString()}");

                return false;
            }

            ColliderTransformConfig.ReloadData(originalDescriptor);

            List<(string fieldName, string displayName)> fields = ColliderTransformConfig.GetCustomColliderFields(originalDescriptor);

            if (fields == null)
            {
                Debug.LogError($"<color=#00FF00>[SDK at Home]</color> Custom collider fields not found.");
                ColliderTransformConfig._inVrcAvatarBuild = false;
                ColliderTransformConfig.ReloadData(originalDescriptor);
                return false;
            }

            if (fields.Count == 0)
            {
                // No custom fields to restore
                ColliderTransformConfig._inVrcAvatarBuild = false;
                ColliderTransformConfig.ReloadData(originalDescriptor);
                return true;
            }

            // We have some fields to copy over
            foreach (var fieldInfo in fields)
            {
                VRCAvatarDescriptor.ColliderConfig? colliderConfig = ColliderTransformConfig.GetColliderConfig(originalDescriptor, fieldInfo.fieldName);

                if (colliderConfig != null)
                {
                    try
                    {
                        string originalPath = GetRelativePath(colliderConfig.Value.transform, originalDescriptor.transform);

                        if (originalPath == null)
                        {
                            Debug.LogError($"<color=#00FF00>[SDK at Home]</color> Couldn't find the path to the collider transforms on the original avatar, as they likely got removed. Your custom colliders will not work.");
                            return false;
                        }

                        Transform newTransform = FindOrCreateByRelativePath(avatar.transform, originalPath);
                        CopyLocalTransformValues(originalDescriptor.transform.Find(originalPath), newTransform);

                        ColliderTransformConfig.SetCustomTransform(descriptor, fieldInfo.fieldName, newTransform);

                        var config = ColliderTransformConfig.GetColliderConfig(descriptor, fieldInfo.fieldName);
                        if (config.HasValue)
                        {
                            var updatedConfig = config.Value;
                            updatedConfig.transform = newTransform;
                            ColliderTransformConfig.SetColliderConfig(descriptor, fieldInfo.fieldName, updatedConfig);
                            EditorUtility.SetDirty(descriptor);
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"<color=#00FF00>[SDK at Home]</color> an exception happened while trying to add the custom colliders back to the avatar: {e.Message}");
                        return false;
                    }
                }
            }

            EditorUtility.SetDirty(descriptor);
            ColliderTransformConfig._inVrcAvatarBuild = false;
            ColliderTransformConfig.ReloadData(originalDescriptor);

            Debug.Log($"<color=#00FF00>[SDK at Home]</color> Successfully applied {CustomFields.Count.ToString()} Custom Colliders when building the avatar.");
            return true;

        }

        #region Helper methods

        public static string GetRelativePath(Transform target, Transform root)
        {
            if (target == null || root == null) return null;
            if (target == root) return "";

            var parts = new List<string>();
            var t = target;

            while (t != null && t != root)
            {
                parts.Add(t.name);
                t = t.parent;
            }

            if (t != root) return null;

            parts.Reverse();
            return string.Join("/", parts);
        }

        public static Transform FindOrCreateByRelativePath(Transform root, string path)
        {
            if (root == null) return null;
            if (string.IsNullOrWhiteSpace(path)) return root;

            path = path.Trim().Replace("\\", "/");
            while (path.StartsWith("/")) path = path.Substring(1);

            var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            Transform cur = root;

            foreach (var part in parts)
            {
                Transform next = null;

                for (int i = 0; i < cur.childCount; i++)
                {
                    var c = cur.GetChild(i);
                    if (string.Equals(c.name, part, StringComparison.Ordinal))
                    {
                        next = c;
                        break;
                    }
                }

                // The transform got destroyed
                // Recreate it
                if (next == null)
                {
                    var go = new GameObject(part);
                    go.transform.SetParent(cur, false);
                    next = go.transform;
                }

                cur = next;
            }

            return cur;
        }

        public static void CopyLocalTransformValues(Transform src, Transform dst)
        {
            if (src == null || dst == null) return;

            dst.localPosition = src.localPosition;
            dst.localRotation = src.localRotation;
            dst.localScale = src.localScale;
        }

        #endregion Helper methods

    }

}

#endif
