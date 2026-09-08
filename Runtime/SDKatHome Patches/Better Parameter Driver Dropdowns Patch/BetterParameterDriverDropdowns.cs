#if UNITY_EDITOR
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEditor;
using UnityEditor.IMGUI.Controls;

namespace Patches
{
    [HarmonyPatch]
    public class BetterParameterDriverDropdowns : SDKPatchBase
    {
        public override string PatchName => "Better Parameter Driver Dropdowns";
        public override string Description => "Choose the dropdown style for parameter driver parameter selection";
        public override string Category => "UI Improvements";
        public override SDKatHomePatcher.PatchUIType UIType => SDKatHomePatcher.PatchUIType.SingleSelect;
        public override string[] Options => new string[] {
            "Custom Advanced (Categories + Search)",
            "Unity Advanced (Built-in with Search)"
        };
        public override int DefaultOption => 1;

        public override string ButtonText => "Configure";
        public override bool UsePrefix => true;
        public override bool UsePostfix => true;

        // Dropdown mode enum
        public enum DropdownMode
        {
            CustomAdvanced = 0,    // Our custom dropdown with categories
            UnityAdvanced = 1      // Unity's built-in AdvancedDropdown
        }

        // Get current dropdown mode from patch configuration
        internal static DropdownMode GetDropdownMode()
        {
            var patchInfo = SDKatHomePatcher.GetPatchInfo(typeof(BetterParameterDriverDropdowns));
            return (DropdownMode)patchInfo.SelectedOption;
        }

        // The custom dropdown tracks its own single instance in SDKatHomeDropdownWindow.
        private static ParameterAdvancedDropdown _activeUnityDropdown;

        public static string[] GetPreferenceKeys()
        {
            return new string[]
            {
                "SDKatHome_Better Parameter Driver Dropdowns",
                "SDKatHome_Better Parameter Driver Dropdowns_Selection"
            };
        }

        // Target the DrawParameterDropdown method
        public static MethodBase TargetMethod()
        {
            // Find the AvatarParameterDriverEditor class
            Type editorType = null;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name.Contains("VRC.SDK3A.Editor"))
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (type.Name == "AvatarParameterDriverEditor")
                        {
                            editorType = type;
                            break;
                        }
                    }
                }
            }

            if (editorType == null)
            {
                Debug.LogError("Failed to find AvatarParameterDriverEditor type");
                return null;
            }

            // Get the DrawParameterDropdown method
            return editorType.GetMethod("DrawParameterDropdown",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        }

        // Prefix - replace the original method
        public static bool Prefix(ref int __result, object __instance, SerializedProperty name, string label)
        {
            try
            {
                // Get parameter names field via reflection
                FieldInfo paramNamesField = __instance.GetType().GetField("parameterNames",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (paramNamesField == null)
                {
                    Debug.LogError("Failed to find parameterNames field");
                    return true; // Run the original method
                }

                string[] parameterNames = paramNamesField.GetValue(__instance) as string[];
                if (parameterNames == null || parameterNames.Length == 0)
                {
                    // No parameters, revert to original method
                    return true;
                }

                // Get dropdown mode from configuration
                DropdownMode mode = GetDropdownMode();

                // Draw based on selected mode
                switch (mode)
                {
                    case DropdownMode.CustomAdvanced:
                        __result = DrawCustomAdvancedDropdown(parameterNames, name, label);
                        break;
                    case DropdownMode.UnityAdvanced:
                        __result = DrawUnityAdvancedDropdown(parameterNames, name, label);
                        break;
                    default:
                        __result = DrawCustomAdvancedDropdown(parameterNames, name, label);
                        break;
                }

                return false; // Skip original method
            }
            catch (Exception e)
            {
                Debug.LogError($"Error in BetterParameterDriverDropdowns: {e.Message}\n{e.StackTrace}");
                return true; // Run original method on error
            }
        }

        // Call the Postfix, which is in the ParameterDriverTextFieldMonitor class
        public static void Postfix(object __instance)
        {
            try
            {
                // Delegate to the text field monitor
                ParameterDriverTextFieldMonitor.MonitorTextFieldChanges(__instance);
            }
            catch (Exception) { }
        }

        public static void TriggerInspectorRefresh(UnityEngine.Object targetObject)
        {
            EditorApplication.delayCall += () =>
            {
                if (targetObject == null) return;

                var inspectorType = typeof(Editor).Assembly.GetType("UnityEditor.InspectorWindow");
                var inspectorWindows = Resources.FindObjectsOfTypeAll(inspectorType);

                foreach (var inspector in inspectorWindows)
                {
                    var trackerField = inspectorType.GetField("m_Tracker", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (trackerField != null)
                    {
                        var tracker = trackerField.GetValue(inspector);
                        if (tracker != null)
                        {
                            var rebuildMethod = tracker.GetType().GetMethod("ForceRebuild");
                            rebuildMethod?.Invoke(tracker, null);
                        }
                    }
                }
            };
        }

        #region ParameterDriverTextFieldMonitor
        [HarmonyPatch]
        public static class ParameterDriverTextFieldMonitor
        {
            private static Dictionary<string, string> _parameterFieldValues = new Dictionary<string, string>();
            private static Dictionary<int, DateTime> _lastChangeTime = new Dictionary<int, DateTime>();
            private static Dictionary<int, UnityEngine.Object> _pendingRefreshTargets = new Dictionary<int, UnityEngine.Object>();
            private static float _debounceDelaySeconds = 0.5f; // 500ms delay

            // Target the OnInspectorGUI method of the Parameter Driver Editor
            public static MethodBase TargetMethod()
            {
                Type editorType = null;

                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.GetName().Name.Contains("VRC.SDK3A.Editor"))
                    {
                        foreach (var type in assembly.GetTypes())
                        {
                            if (type.Name == "AvatarParameterDriverEditor")
                            {
                                editorType = type;
                                break;
                            }
                        }
                    }
                }

                if (editorType == null)
                    return null;

                return editorType.GetMethod("OnInspectorGUI",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }

            // Postfix to check for changes after the Inspector GUI is drawn
            public static void MonitorTextFieldChanges(object __instance)
            {
                try
                {
                    // Get the serialized object
                    SerializedObject serializedObject = GetSerializedObjectFromEditor(__instance);

                    if (serializedObject == null) return;

                    // Check all parameter entries for changes
                    CheckParameterEntriesForChanges(serializedObject);
                }
                catch (Exception) { }
            }

            private static SerializedObject GetSerializedObjectFromEditor(object editorInstance)
            {
                try
                {
                    Type editorType = editorInstance.GetType();


                    var field = editorType.GetField("m_SerializedObject", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (field != null)
                    {
                        var serializedObject = field.GetValue(editorInstance) as SerializedObject;
                        if (serializedObject != null)
                        {
                            return serializedObject;
                        }
                    }

                    return null;
                }
                catch (Exception)
                {
                    return null;
                }
            }

            private static void CheckParameterEntriesForChanges(SerializedObject serializedObject)
            {
                DropdownMode mode = GetDropdownMode();
                if (mode != DropdownMode.CustomAdvanced) return;

                // Find the parameters array property
                var parametersProperty = serializedObject.FindProperty("parameters");
                if (parametersProperty == null || !parametersProperty.isArray) return;

                bool hasChanges = false;
                string objectKey = serializedObject.targetObject.GetInstanceID().ToString();

                // Check each parameter entry
                for (int i = 0; i < parametersProperty.arraySize; i++)
                {
                    var parameterEntry = parametersProperty.GetArrayElementAtIndex(i);
                    var nameProperty = parameterEntry.FindPropertyRelative("name");

                    if (nameProperty == null || nameProperty.propertyType != SerializedPropertyType.String)
                    {
                        continue;
                    }

                    string propertyKey = $"{objectKey}_{i}_name";
                    string currentValue = nameProperty.stringValue;

                    if (_parameterFieldValues.TryGetValue(propertyKey, out string previousValue))
                    {
                        if (previousValue != currentValue)
                        {
                            _parameterFieldValues[propertyKey] = currentValue;
                            hasChanges = true;
                        }
                    }
                    else
                    {
                        _parameterFieldValues[propertyKey] = currentValue;
                    }
                }

                // If any changes detected, trigger debounced refresh
                if (hasChanges)
                {
                    ScheduleDebouncedRefresh(serializedObject.targetObject);
                }
            }

            private static void ScheduleDebouncedRefresh(UnityEngine.Object targetObject)
            {
                if (targetObject == null) return;

                int instanceId = targetObject.GetInstanceID();

                // Update the last change time for this object
                _lastChangeTime[instanceId] = System.DateTime.Now;
                _pendingRefreshTargets[instanceId] = targetObject;

                // Schedule the debounced check
                EditorApplication.delayCall += () => CheckAndExecuteDebouncedRefresh(instanceId);
            }

            private static void CheckAndExecuteDebouncedRefresh(int instanceId)
            {
                // Check if this object still has pending changes
                if (!_lastChangeTime.ContainsKey(instanceId) || !_pendingRefreshTargets.ContainsKey(instanceId))
                    return;

                var lastChange = _lastChangeTime[instanceId];
                var timeSinceLastChange = System.DateTime.Now - lastChange;

                if (timeSinceLastChange.TotalSeconds >= _debounceDelaySeconds)
                {
                    // Enough time has passed, execute the refresh
                    var targetObject = _pendingRefreshTargets[instanceId];

                    if (targetObject != null)
                    {
                        TriggerInspectorRefresh(targetObject);
                    }

                    // Clean up
                    _lastChangeTime.Remove(instanceId);
                    _pendingRefreshTargets.Remove(instanceId);
                }
                else
                {
                    // Not enough time has passed, schedule another check
                    var remainingTime = _debounceDelaySeconds - timeSinceLastChange.TotalSeconds;
                    EditorApplication.delayCall += () => CheckAndExecuteDebouncedRefresh(instanceId);
                }
            }

            private static bool _isUpdateLoopRunning = false;

            private static void DebouncedRefreshUpdateLoop()
            {
                var currentTime = System.DateTime.Now;
                var keysToProcess = new List<int>();

                // Check all pending refreshes
                foreach (var kvp in _lastChangeTime.ToList())
                {
                    var instanceId = kvp.Key;
                    var lastChangeTime = kvp.Value;

                    var timeSinceLastChange = currentTime - lastChangeTime;

                    if (timeSinceLastChange.TotalSeconds >= _debounceDelaySeconds)
                    {
                        keysToProcess.Add(instanceId);
                    }
                }

                // Process refreshes that are ready
                foreach (var instanceId in keysToProcess)
                {
                    if (_pendingRefreshTargets.TryGetValue(instanceId, out var targetObject) && targetObject != null)
                    {
                        TriggerInspectorRefresh(targetObject);
                    }

                    // Clean up
                    _lastChangeTime.Remove(instanceId);
                    _pendingRefreshTargets.Remove(instanceId);
                }

                // Stop the update loop if no more pending refreshes
                if (_lastChangeTime.Count == 0)
                {
                    _isUpdateLoopRunning = false;
                    EditorApplication.update -= DebouncedRefreshUpdateLoop;
                }
            }

            // Clean up tracking when editors are disabled
            // Enhanced cleanup to handle debouncing data:
            [HarmonyPatch(typeof(Editor), "OnDisable")]
            private static class EditorCloseCleanupPatch
            {
                public static void Prefix(Editor __instance)
                {
                    if (__instance.GetType().Name.Contains("ParameterDriver"))
                    {
                        // Clean up tracking dictionary for this editor instance
                        var instanceId = __instance.target.GetInstanceID();

                        // Clean up field value tracking
                        var keysToRemove = _parameterFieldValues.Keys
                            .Where(key => key.StartsWith($"{instanceId}_"))
                            .ToList();

                        foreach (var key in keysToRemove)
                        {
                            _parameterFieldValues.Remove(key);
                        }

                        // Clean up debouncing data
                        _lastChangeTime.Remove(instanceId);
                        _pendingRefreshTargets.Remove(instanceId);

                        // Stop update loop if no more pending refreshes
                        if (_lastChangeTime.Count == 0 && _isUpdateLoopRunning)
                        {
                            _isUpdateLoopRunning = false;
                            EditorApplication.update -= DebouncedRefreshUpdateLoop;
                        }
                    }
                }
            }
        }
        #endregion

        #region Custom Advanced Dropdown Implementation

        private static int DrawCustomAdvancedDropdown(string[] parameterNames, SerializedProperty name, string label)
        {
            // Get the display name (last part after /)
            string displayName = name.stringValue;
            string[] pathParts = displayName.Split('/');
            if (pathParts.Length > 1)
            {
                displayName = pathParts[pathParts.Length - 1];
            }

            // Draw the main property field
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(label);

            // Create dropdown button
            Rect buttonRect = GUILayoutUtility.GetRect(new GUIContent(displayName), EditorStyles.popup, GUILayout.Width(160));

            if (EditorGUI.DropdownButton(buttonRect, new GUIContent(displayName), FocusType.Keyboard, EditorStyles.popup))
            {
                // Close existing dropdown if open
                CloseActiveDropdown();

                // Show custom dropdown window
                ShowCustomDropdownWindow(buttonRect, parameterNames, name);
            }

            EditorGUILayout.EndHorizontal();

            // Full path text field on a separate line
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(" "); // Empty space for alignment
            name.stringValue = EditorGUILayout.TextField(name.stringValue);
            EditorGUILayout.EndHorizontal();

            // Find current index for return value
            int currentIndex = 0;
            for (int i = 0; i < parameterNames.Length; i++)
            {
                if (parameterNames[i] == name.stringValue)
                {
                    currentIndex = i;
                    break;
                }
            }

            return currentIndex;
        }

        private static void ShowCustomDropdownWindow(Rect buttonRect, string[] parameters, SerializedProperty property)
        {
            try
            {
                // Close existing dropdown first
                CloseActiveDropdown();

                // Validate inputs before creating window
                if (parameters == null || parameters.Length == 0)
                {
                    Debug.LogWarning("No parameters available for dropdown");
                    return;
                }

                if (property == null || property.serializedObject == null || property.serializedObject.targetObject == null)
                {
                    Debug.LogWarning("Invalid property for dropdown");
                    return;
                }

                // The window only reports which item was picked; writing the property stays here,
                // captured off the SerializedProperty before it can be disposed.
                string propertyPath = property.propertyPath;
                UnityEngine.Object targetObject = property.serializedObject.targetObject;
                string currentValue = property.stringValue;
                string[] items = parameters;

                SDKatHome.SDKatHomeDropdownWindow.Show(
                    buttonRect, items, currentValue,
                    index => ApplyParameterSelection(items, index, targetObject, propertyPath));
            }
            catch (Exception e)
            {
                Debug.LogError($"Error showing custom dropdown window: {e.Message}");
            }
        }

        /// <summary>
        /// Writes the picked parameter back to the driver. This used to live inside the dropdown
        /// window; it moved out when the window became shared, because nothing about writing a
        /// SerializedProperty belongs in a generic list widget. A fresh SerializedObject is built
        /// from the captured target rather than holding the original property, which can be
        /// disposed while the dropdown is open.
        /// </summary>
        private static void ApplyParameterSelection(string[] parameters, int index,
            UnityEngine.Object targetObject, string propertyPath)
        {
            if (parameters == null || index < 0 || index >= parameters.Length) return;
            if (targetObject == null || string.IsNullOrEmpty(propertyPath)) return;

            string parameter = parameters[index];
            if (parameter == null) return;

            try
            {
                using (var serializedObject = new SerializedObject(targetObject))
                {
                    var property = serializedObject.FindProperty(propertyPath);

                    if (property == null || property.propertyType != SerializedPropertyType.String)
                    {
                        Debug.LogError($"Could not find string property at path: {propertyPath}");
                        return;
                    }

                    property.stringValue = parameter;
                    serializedObject.ApplyModifiedProperties();
                }

                EditorApplication.delayCall += () =>
                {
                    try
                    {
                        if (targetObject != null) TriggerInspectorRefresh(targetObject);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"Error refreshing inspector: {e.Message}");
                    }
                };
            }
            catch (Exception e)
            {
                Debug.LogError($"Error selecting parameter '{parameter}': {e.Message}");
            }
        }

        #endregion

        #region Unity Advanced Dropdown Implementation

        private static int DrawUnityAdvancedDropdown(string[] parameterNames, SerializedProperty name, string label)
        {
            // Get the display name (last part after /)
            string displayName = name.stringValue;
            string[] pathParts = displayName.Split('/');
            if (pathParts.Length > 1)
            {
                displayName = pathParts[pathParts.Length - 1];
            }

            // Draw the main property field
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(label);

            // Create dropdown button
            Rect buttonRect = GUILayoutUtility.GetRect(new GUIContent(displayName), EditorStyles.popup, GUILayout.Width(160));

            if (EditorGUI.DropdownButton(buttonRect, new GUIContent(displayName), FocusType.Keyboard, EditorStyles.popup))
            {
                // Close existing dropdown if open
                CloseActiveUnityDropdown();

                // Show Unity's AdvancedDropdown
                ShowUnityAdvancedDropdown(buttonRect, parameterNames, name);
            }

            EditorGUILayout.EndHorizontal();

            // Full path text field on a separate line
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(" "); // Empty space for alignment
            name.stringValue = EditorGUILayout.TextField(name.stringValue);
            EditorGUILayout.EndHorizontal();

            // Find current index for return value
            int currentIndex = 0;
            for (int i = 0; i < parameterNames.Length; i++)
            {
                if (parameterNames[i] == name.stringValue)
                {
                    currentIndex = i;
                    break;
                }
            }

            return currentIndex;
        }

        private static void ShowUnityAdvancedDropdown(Rect buttonRect, string[] parameters, SerializedProperty property)
        {
            try
            {
                // Create Unity's AdvancedDropdown
                _activeUnityDropdown = new ParameterAdvancedDropdown(new AdvancedDropdownState());
                _activeUnityDropdown.Initialize(parameters, property);

                // Show at button position
                _activeUnityDropdown.Show(buttonRect);
            }
            catch (Exception e)
            {
                Debug.LogError($"Error showing Unity advanced dropdown: {e.Message}");
                _activeUnityDropdown = null;
            }
        }

        #endregion

        #region Cleanup Methods

        // Close active custom dropdown in a safe way
        private static void CloseActiveDropdown()
        {
            SDKatHome.SDKatHomeDropdownWindow.CloseActive();
        }

        // Close active Unity dropdown
        private static void CloseActiveUnityDropdown()
        {
            if (_activeUnityDropdown != null)
            {
                try
                {
                    _activeUnityDropdown = null;
                }
                catch (Exception)
                {
                    // Ignore exceptions during close
                }
            }
        }

        #endregion

        #region Unity AdvancedDropdown Implementation

        // Unity's AdvancedDropdown implementation for parameters
        public class ParameterAdvancedDropdown : AdvancedDropdown
        {
            private string[] _parameters;
            private SerializedProperty _targetProperty;
            private Dictionary<int, string> _idToParameter = new Dictionary<int, string>();
            private int _nextId = 1;

            public ParameterAdvancedDropdown(AdvancedDropdownState state) : base(state)
            {
                minimumSize = new Vector2(200, 300);
            }

            public void Initialize(string[] parameters, SerializedProperty property)
            {
                _parameters = parameters;
                _targetProperty = property;
                _idToParameter.Clear();
                _nextId = 1;
            }

            protected override AdvancedDropdownItem BuildRoot()
            {
                var root = new AdvancedDropdownItem("Parameters");

                // Sort parameters for better organization
                var sortedParameters = _parameters.OrderBy(p => p).ToArray();

                // Group parameters by category
                var categoryGroups = new Dictionary<string, AdvancedDropdownItem>();

                foreach (var parameter in sortedParameters)
                {
                    if (string.IsNullOrEmpty(parameter)) continue;

                    // Split parameter path
                    var parts = parameter.Split('/');

                    if (parts.Length == 1)
                    {
                        // Root level parameter
                        var item = new AdvancedDropdownItem(parameter);
                        item.id = _nextId++;
                        _idToParameter[item.id] = parameter;
                        root.AddChild(item);
                    }
                    else
                    {
                        // Categorized parameter
                        AdvancedDropdownItem currentParent = root;
                        string currentPath = "";

                        // Build category hierarchy
                        for (int i = 0; i < parts.Length - 1; i++)
                        {
                            string categoryName = parts[i];
                            currentPath = string.IsNullOrEmpty(currentPath) ? categoryName : currentPath + "/" + categoryName;

                            if (!categoryGroups.ContainsKey(currentPath))
                            {
                                var categoryItem = new AdvancedDropdownItem(categoryName);
                                categoryGroups[currentPath] = categoryItem;
                                currentParent.AddChild(categoryItem);
                            }

                            currentParent = categoryGroups[currentPath];
                        }

                        // Add the parameter to its category
                        string parameterName = parts[parts.Length - 1];
                        var paramItem = new AdvancedDropdownItem(parameterName);
                        paramItem.id = _nextId++;
                        _idToParameter[paramItem.id] = parameter;
                        currentParent.AddChild(paramItem);
                    }
                }

                return root;
            }

            protected override void ItemSelected(AdvancedDropdownItem item)
            {
                if (_idToParameter.TryGetValue(item.id, out string parameter))
                {
                    try
                    {
                        if (_targetProperty != null && _targetProperty.serializedObject != null)
                        {
                            _targetProperty.stringValue = parameter;
                            _targetProperty.serializedObject.ApplyModifiedProperties();
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"Error selecting parameter: {e.Message}");
                    }
                }

                _activeUnityDropdown = null;
            }
        }

        #endregion

        // The searchable tree dropdown this patch used to own now lives in
        // Runtime/Shared/SDKatHomeDropdownWindow.cs so other patches can use it too.
        // Behaviour is unchanged; it reports the selected index instead of writing the
        // SerializedProperty itself, so the write stayed here in ShowCustomDropdownWindow.

        #region Cleanup Patches

        // Clean up when editor is closed
        [HarmonyPatch(typeof(UnityEditor.Editor), "OnDisable")]
        private static class EditorCloseResetPatch
        {
            public static void Prefix(UnityEditor.Editor __instance)
            {
                if (__instance.GetType().Name == "AvatarParameterDriverEditor")
                {
                    CloseActiveDropdown();
                    CloseActiveUnityDropdown();
                }
            }
        }

        #endregion
    }
}
#endif