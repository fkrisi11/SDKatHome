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

        // Singleton instance of the active dropdown to prevent multiple instances
        private static EditorWindow _activeDropdown;
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

                // Create window
                var window = EditorWindow.CreateInstance<ParameterDropdownWindow>();
                _activeDropdown = window;

                // Initialize with the property - the window will extract what it needs
                window.Initialize(parameters, property);

                // Calculate position in screen space
                Vector2 screenPos = GUIUtility.GUIToScreenPoint(new Vector2(buttonRect.x, buttonRect.y + buttonRect.height));
                window.position = new Rect(screenPos.x, screenPos.y, 250, 300);

                // Show as utility window without a close button
                window.ShowPopup();
            }
            catch (Exception e)
            {
                Debug.LogError($"Error showing custom dropdown window: {e.Message}");
                _activeDropdown = null;
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
            if (_activeDropdown != null)
            {
                try
                {
                    var windowToClose = _activeDropdown;
                    _activeDropdown = null;
                    windowToClose.Close();
                }
                catch (Exception)
                {
                    // Ignore exceptions during close
                }
            }
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

        #region Custom Dropdown Window (Existing Implementation)

        // Parameter dropdown window (existing implementation from original code)
        public class ParameterDropdownWindow : EditorWindow
        {
            // Data
            private string[] _allParameters;
            private SerializedProperty _targetProperty;
            private Dictionary<string, bool> _categoryExpanded = new Dictionary<string, bool>();

            // Keep track of all unique category paths (to make them selectable)
            private HashSet<string> _allCategories = new HashSet<string>();

            // State
            private string _searchText = "";
            private Vector2 _scrollPosition;

            // Categories organized as a tree structure
            private class CategoryNode
            {
                public string Name { get; set; }
                public string FullPath { get; set; }
                public List<CategoryNode> SubCategories { get; set; } = new List<CategoryNode>();
                public List<string> Parameters { get; set; } = new List<string>();
                public bool IsExpanded { get; set; } = false;
                public bool IsSelectable { get; set; } = false; // Whether this category is also a parameter
            }
            private CategoryNode _rootCategory;

            // Styling
            private GUIStyle _categoryStyle;
            private GUIStyle _itemStyle;
            private GUIStyle _selectedItemStyle;
            private GUIStyle _searchResultStyle;
            private GUIStyle _selectableCategoryStyle;
            private GUIStyle _selectedCategoryStyle;

            private bool _shouldScrollToSelected = false;
            private string _selectedParameterPath = "";
            private float _selectedParameterYPosition = 0f;
            private string _initialSelectedParameter = "";
            private string _targetPropertyPath = "";
            private UnityEngine.Object _targetObject = null;

            // Initialize
            public void Initialize(string[] parameters, SerializedProperty property)
            {
                _allParameters = parameters;

                // Store the data we need instead of keeping the SerializedProperty reference
                if (property != null && property.serializedObject != null && property.serializedObject.targetObject != null)
                {
                    _targetPropertyPath = property.propertyPath;
                    _targetObject = property.serializedObject.targetObject;
                    _selectedParameterPath = property.stringValue;
                    _initialSelectedParameter = property.stringValue;
                }
                else
                {
                    _targetPropertyPath = "";
                    _targetObject = null;
                    _selectedParameterPath = "";
                    _initialSelectedParameter = "";
                }

                // Don't store the SerializedProperty reference - it can become disposed
                _targetProperty = null;

                _searchText = "";
                _scrollPosition = Vector2.zero;
                _allCategories.Clear();

                _shouldScrollToSelected = !string.IsNullOrEmpty(_selectedParameterPath);

                // Sort parameters
                Array.Sort(_allParameters);

                // Find all unique category paths first
                foreach (var param in _allParameters)
                {
                    int lastSlash = param.LastIndexOf('/');
                    if (lastSlash > 0)
                    {
                        string categoryPath = param.Substring(0, lastSlash);
                        _allCategories.Add(categoryPath);

                        // Also add all parent categories
                        string[] parts = categoryPath.Split('/');
                        string currentPath = "";

                        for (int i = 0; i < parts.Length; i++)
                        {
                            if (i > 0) currentPath += "/";
                            currentPath += parts[i];
                            _allCategories.Add(currentPath);
                        }
                    }
                }

                // Build category tree
                BuildCategoryTree();

                if (_shouldScrollToSelected && Array.IndexOf(_allParameters, _selectedParameterPath) >= 0)
                {
                    ExpandPathToParameter(_selectedParameterPath);
                }

                // Initialize styles
                InitializeStyles();
            }

            // Expand the path to a specific parameter
            private void ExpandPathToParameter(string parameterPath)
            {
                if (string.IsNullOrEmpty(parameterPath)) return;

                // Find the last slash to get the category path
                int lastSlashIndex = parameterPath.LastIndexOf('/');
                if (lastSlashIndex <= 0) return; // Root level parameter, no expansion needed

                string categoryPath = parameterPath.Substring(0, lastSlashIndex);

                // Expand all parent categories
                string[] pathParts = categoryPath.Split('/');
                string currentPath = "";

                for (int i = 0; i < pathParts.Length; i++)
                {
                    if (i > 0) currentPath += "/";
                    currentPath += pathParts[i];

                    // Set this category as expanded
                    _categoryExpanded[currentPath] = true;

                    // Also update the category node
                    UpdateCategoryNodeExpansion(_rootCategory, currentPath, true);
                }
            }

            // Update category node expansion state
            private void UpdateCategoryNodeExpansion(CategoryNode node, string targetPath, bool expanded)
            {
                if (node.FullPath == targetPath)
                {
                    node.IsExpanded = expanded;
                    return;
                }

                foreach (var subCategory in node.SubCategories)
                {
                    UpdateCategoryNodeExpansion(subCategory, targetPath, expanded);
                }
            }

            // Initialize GUI styles
            private void InitializeStyles()
            {
                // Regular category style (bold with foldout)
                _categoryStyle = new GUIStyle(EditorStyles.foldout);
                _categoryStyle.fontStyle = FontStyle.Bold;

                // Regular parameter style
                _itemStyle = new GUIStyle(EditorStyles.label);

                // Selected parameter style
                _selectedItemStyle = new GUIStyle(_itemStyle);
                _selectedItemStyle.fontStyle = FontStyle.Bold;
                _selectedItemStyle.normal.textColor = EditorGUIUtility.isProSkin ?
                                                   Color.cyan :
                                                   new Color(0, 0.5f, 0.8f);

                // Search result style
                _searchResultStyle = new GUIStyle(_itemStyle);
                _searchResultStyle.fontSize = _itemStyle.fontSize;

                // Selectable category style (bold with foldout, but also selectable)
                _selectableCategoryStyle = new GUIStyle(_categoryStyle);
                _selectableCategoryStyle.normal.textColor = EditorGUIUtility.isProSkin ?
                                                         new Color(0.9f, 0.9f, 0.5f) :
                                                         new Color(0.6f, 0.6f, 0.0f);

                // Selected category style
                _selectedCategoryStyle = new GUIStyle(_selectableCategoryStyle);
                _selectedCategoryStyle.normal.textColor = EditorGUIUtility.isProSkin ?
                                                       Color.cyan :
                                                       new Color(0, 0.5f, 0.8f);
            }

            // Build the category tree from parameter paths
            private void BuildCategoryTree()
            {
                _rootCategory = new CategoryNode
                {
                    Name = "Root",
                    FullPath = ""
                };

                foreach (var param in _allParameters)
                {
                    AddParameterToTree(param);
                }

                // Mark categories as selectable if they exist as parameters
                MarkSelectableCategories(_rootCategory);
            }

            // Mark categories that are also parameters as selectable
            private void MarkSelectableCategories(CategoryNode node)
            {
                // Check if this category path is also a parameter
                if (!string.IsNullOrEmpty(node.FullPath) && Array.IndexOf(_allParameters, node.FullPath) >= 0)
                {
                    node.IsSelectable = true;
                }

                // Process subcategories
                foreach (var subCategory in node.SubCategories)
                {
                    MarkSelectableCategories(subCategory);
                }
            }

            // Add a parameter to the category tree
            private void AddParameterToTree(string param)
            {
                string[] parts = param.Split('/');

                if (parts.Length == 1)
                {
                    // Root level parameter
                    _rootCategory.Parameters.Add(param);
                    return;
                }

                // Build the category path
                CategoryNode currentNode = _rootCategory;
                string currentPath = "";

                // Create or navigate to each level of the category hierarchy
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    string part = parts[i];
                    currentPath = string.IsNullOrEmpty(currentPath) ? part : currentPath + "/" + part;

                    // Find existing subcategory
                    CategoryNode subCategory = currentNode.SubCategories.FirstOrDefault(c => c.Name == part);

                    if (subCategory == null)
                    {
                        // Create new subcategory
                        subCategory = new CategoryNode
                        {
                            Name = part,
                            FullPath = currentPath,
                            IsExpanded = _categoryExpanded.ContainsKey(currentPath) ? _categoryExpanded[currentPath] : false
                        };
                        currentNode.SubCategories.Add(subCategory);
                    }

                    currentNode = subCategory;
                }

                // Add the parameter to the final category
                currentNode.Parameters.Add(param);
            }

            // Draw the window
            private void OnGUI()
            {
                // Only filter out the most problematic events, but allow clicks and important events
                if (Event.current.type == EventType.DragUpdated ||
                    Event.current.type == EventType.DragPerform ||
                    Event.current.type == EventType.DragExited)
                {
                    return;
                }

                try
                {
                    // Use Unity's window background style for a built-in border look
                    GUI.Box(new Rect(0, 0, position.width, position.height), "", EditorStyles.helpBox);

                    // Add padding inside the border using GUILayout
                    GUILayout.BeginArea(new Rect(4, 4, position.width - 8, position.height - 8));

                    try
                    {
                        // Handle keyboard events
                        if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
                        {
                            Close();
                            return;
                        }

                        // Draw search field
                        DrawSearchField();

                        // Begin scroll view
                        using (var scrollScope = new EditorGUILayout.ScrollViewScope(_scrollPosition))
                        {
                            _scrollPosition = scrollScope.scrollPosition;

                            // Show search results or hierarchical view
                            if (!string.IsNullOrEmpty(_searchText))
                            {
                                DrawSearchResults();
                            }
                            else
                            {
                                DrawCategoryTree();
                            }
                        }
                    }
                    finally
                    {
                        GUILayout.EndArea();
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"GUI Error in ParameterDropdownWindow: {e.Message}\n{e.StackTrace}");
                    Close();
                }
            }

            // Draw search field with integrated clear button
            private void DrawSearchField()
            {
                EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
                GUILayout.Label("Search:", GUILayout.Width(50));

                // Track if we should clear the search
                bool clearSearch = false;

                // First get a rect for the entire search area
                Rect searchAreaRect = GUILayoutUtility.GetRect(new GUIContent(" "), EditorStyles.toolbarSearchField, GUILayout.ExpandWidth(true));

                // Split it into search field and button parts
                Rect searchFieldRect = searchAreaRect;
                searchFieldRect.width -= 20; // Reserve space for button

                Rect clearButtonRect = searchAreaRect;
                clearButtonRect.x = searchFieldRect.xMax;
                clearButtonRect.width = 20;

                // Create button style
                GUIStyle clearButtonStyle = new GUIStyle(EditorStyles.toolbarButton);
                clearButtonStyle.fontSize = 16;
                clearButtonStyle.alignment = TextAnchor.MiddleCenter;
                clearButtonStyle.normal.textColor = Color.gray;
                clearButtonStyle.hover.textColor = Color.white;
                clearButtonStyle.active.textColor = Color.white;

                // Draw text field
                GUI.SetNextControlName("SearchField");
                string newSearchText = EditorGUI.TextField(searchFieldRect, _searchText, EditorStyles.toolbarSearchField);
                GUI.FocusControl("SearchField");

                // Draw the clear button (as a real button)
                if (!string.IsNullOrEmpty(_searchText))
                {
                    // Draw a real clickable button
                    if (GUI.Button(clearButtonRect, "×", clearButtonStyle))
                    {
                        clearSearch = true;
                        Event.current.Use(); // Consume the event
                    }

                    // Show hand cursor on hover
                    EditorGUIUtility.AddCursorRect(clearButtonRect, MouseCursor.Link);
                }

                // Apply search text changes
                if (clearSearch)
                {
                    newSearchText = "";
                    GUI.FocusControl(null);
                }

                if (newSearchText != _searchText)
                {
                    _searchText = newSearchText;
                    _scrollPosition = Vector2.zero; // Reset scroll position when search changes
                }

                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space();
            }

            private void DrawSearchResults()
            {
                try
                {
                    List<string> results = GetSearchResults();
                    EditorGUILayout.LabelField($"Search Results ({results.Count}):", EditorStyles.boldLabel);
                    EditorGUILayout.Space();

                    foreach (string result in results)
                    {
                        // Skip null/empty results
                        if (string.IsNullOrEmpty(result)) continue;

                        GUILayout.BeginHorizontal();
                        GUILayout.Space(15);

                        // Use stored initial selection to avoid accessing disposed property
                        bool isSelected = result == _initialSelectedParameter;
                        GUIStyle style = isSelected ? _selectedItemStyle : _searchResultStyle;

                        // Draw the button normally
                        if (GUILayout.Button(result, style))
                        {
                            SelectParameterSafely(result);
                            return; // Exit since window will close
                        }

                        GUILayout.EndHorizontal();
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"Error in DrawSearchResults: {e.Message}");
                }
            }

            private List<string> GetSearchResults()
            {
                List<string> results = new List<string>();

                foreach (string param in _allParameters)
                {
                    if (param.ToLowerInvariant().Contains(_searchText.ToLowerInvariant()))
                    {
                        results.Add(param);
                    }
                }

                foreach (string category in _allCategories)
                {
                    if (category.ToLowerInvariant().Contains(_searchText.ToLowerInvariant()) &&
                        !results.Contains(category) &&
                        Array.IndexOf(_allParameters, category) >= 0)
                    {
                        results.Add(category);
                    }
                }

                results.Sort();
                return results;
            }

            private void SelectParameterSafely(string param)
            {
                try
                {
                    // First check if the parameter itself is valid
                    if (param == null)
                    {
                        Debug.LogError("Cannot select parameter: parameter is null");
                        Close();
                        return;
                    }

                    // Check if we have the target object and property path
                    if (_targetObject == null || string.IsNullOrEmpty(_targetPropertyPath))
                    {
                        Debug.LogWarning("Target object or property path is missing - closing dropdown");
                        Close();
                        return;
                    }

                    // Create a fresh SerializedObject and find the property
                    try
                    {
                        using (var serializedObject = new SerializedObject(_targetObject))
                        {
                            var property = serializedObject.FindProperty(_targetPropertyPath);

                            if (property != null && property.propertyType == SerializedPropertyType.String)
                            {
                                property.stringValue = param;
                                serializedObject.ApplyModifiedProperties();

                                // Close window immediately
                                Close();

                                // Schedule refresh
                                EditorApplication.delayCall += () =>
                                {
                                    try
                                    {
                                        if (_targetObject != null)
                                        {
                                            BetterParameterDriverDropdowns.TriggerInspectorRefresh(_targetObject);
                                        }
                                    }
                                    catch (System.Exception e)
                                    {
                                        Debug.LogError($"Error refreshing inspector: {e.Message}");
                                    }
                                };
                            }
                            else
                            {
                                Debug.LogError($"Could not find string property at path: {_targetPropertyPath}");
                                Close();
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"Error creating SerializedObject or finding property: {e.Message}");
                        Close();
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"Error selecting parameter '{param ?? "NULL"}': {e.Message}");
                    Close();
                }
            }

            // Draw the entire category tree
            private void DrawCategoryTree()
            {
                _selectedParameterYPosition = 0f;
                float currentY = 0f;

                // Draw root-level parameters
                foreach (string param in _rootCategory.Parameters)
                {
                    if (param == _initialSelectedParameter)
                    {
                        _selectedParameterYPosition = currentY;
                    }

                    DrawParameterItem(param, 0, ref currentY);
                }

                // If there are both root parameters and subcategories, add separator
                if (_rootCategory.Parameters.Count > 0 && _rootCategory.SubCategories.Count > 0)
                {
                    EditorGUILayout.Space();
                    currentY += EditorGUIUtility.singleLineHeight;
                    EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
                    EditorGUILayout.Space();
                    currentY += EditorGUIUtility.singleLineHeight;
                }

                // Draw subcategories
                foreach (CategoryNode category in _rootCategory.SubCategories.OrderBy(c => c.Name))
                {
                    DrawCategoryNode(category, 0, ref currentY);
                }

                // Handle scrolling to selected parameter - do this immediately, not in delayCall
                if (_shouldScrollToSelected && _selectedParameterYPosition > 0)
                {
                    ScrollToSelectedParameter();
                    _shouldScrollToSelected = false; // Only scroll once
                }
            }

            // Scrolling to the selected parameter
            private void ScrollToSelectedParameter()
            {
                try
                {
                    // Calculate the desired scroll position
                    float windowHeight = position.height - 60; // Account for search field and padding
                    float targetScrollY = _selectedParameterYPosition - (windowHeight * 0.3f); // Scroll so item is in upper third

                    // Clamp the scroll position
                    targetScrollY = Mathf.Max(0, targetScrollY);

                    _scrollPosition.y = targetScrollY;

                    // Force immediate repaint
                    Repaint();
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"Error scrolling to selected parameter: {e.Message}");
                }
            }

            // Draw a category node with its parameters and subcategories
            private void DrawCategoryNode(CategoryNode category, int indentLevel, ref float currentY)
            {
                try
                {
                    // Add null check for category
                    if (category == null || string.IsNullOrEmpty(category.Name))
                    {
                        Debug.LogWarning("Skipping null or empty category");
                        return;
                    }

                    GUILayout.BeginHorizontal();

                    // Add indentation
                    GUILayout.Space(15 * indentLevel);

                    // Store expansion state
                    bool wasExpanded = category.IsExpanded;
                    bool isSelectable = !string.IsNullOrEmpty(category.FullPath) &&
                                       Array.IndexOf(_allParameters, category.FullPath) >= 0;

                    // Use stored initial selection to avoid accessing disposed property
                    bool isSelected = isSelectable && category.FullPath == _initialSelectedParameter;

                    // Track position for selected category
                    if (isSelected)
                    {
                        _selectedParameterYPosition = currentY;
                    }

                    // Choose style
                    GUIStyle style = isSelectable
                        ? (isSelected ? _selectedCategoryStyle : _selectableCategoryStyle)
                        : _categoryStyle;

                    // Draw foldout
                    bool isExpanded;
                    if (isSelectable)
                    {
                        // For selectable categories, manually set the color
                        Color originalColor = GUI.contentColor;

                        if (isSelected)
                        {
                            // Blue color when this category is the selected parameter
                            GUI.contentColor = EditorGUIUtility.isProSkin ? Color.cyan : new Color(0, 0.5f, 0.8f);
                        }
                        else
                        {
                            // Yellow color when selectable but not selected
                            GUI.contentColor = EditorGUIUtility.isProSkin ?
                                new Color(0.9f, 0.9f, 0.5f) :
                                new Color(0.6f, 0.6f, 0.0f);
                        }

                        isExpanded = EditorGUILayout.Foldout(wasExpanded, category.Name, true, _categoryStyle);
                        GUI.contentColor = originalColor;
                    }
                    else
                    {
                        // Regular non-selectable category
                        isExpanded = EditorGUILayout.Foldout(wasExpanded, category.Name, true, _categoryStyle);
                    }

                    // Draw select button if selectable
                    if (isSelectable && !string.IsNullOrEmpty(category.FullPath))
                    {
                        if (GUILayout.Button("Select", EditorStyles.miniButton, GUILayout.Width(50)))
                        {
                            SelectParameterSafely(category.FullPath);
                            return; // Exit since window will close
                        }
                    }

                    GUILayout.EndHorizontal();
                    currentY += EditorGUIUtility.singleLineHeight;

                    // Update expansion state
                    if (isExpanded != wasExpanded)
                    {
                        category.IsExpanded = isExpanded;
                        if (!string.IsNullOrEmpty(category.FullPath))
                        {
                            _categoryExpanded[category.FullPath] = isExpanded;
                        }
                    }

                    // Show contents if expanded
                    if (isExpanded)
                    {
                        if (category.Parameters != null)
                        {
                            foreach (string param in category.Parameters.OrderBy(p => p))
                            {
                                if (string.IsNullOrEmpty(param)) continue; // Skip null/empty parameters

                                if (param == _initialSelectedParameter)
                                {
                                    _selectedParameterYPosition = currentY;
                                }
                                DrawParameterItem(param, indentLevel + 1, ref currentY);
                            }
                        }

                        if (category.SubCategories != null)
                        {
                            foreach (CategoryNode subCategory in category.SubCategories.OrderBy(c => c.Name))
                            {
                                DrawCategoryNode(subCategory, indentLevel + 1, ref currentY);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"Error drawing category node '{category?.Name ?? "NULL"}': {e.Message}");
                    currentY += EditorGUIUtility.singleLineHeight;
                    // Try to end the horizontal layout if it was started
                    try { GUILayout.EndHorizontal(); } catch { }
                }
            }

            // Draw a parameter item
            private void DrawParameterItem(string param, int indentLevel, ref float currentY)
            {
                try
                {
                    // Add null check for parameter
                    if (string.IsNullOrEmpty(param))
                    {
                        Debug.LogWarning("Skipping null or empty parameter");
                        return;
                    }

                    GUILayout.BeginHorizontal();

                    // Add indentation
                    GUILayout.Space(30 + (indentLevel * 15));

                    // Get display name
                    string displayName = param;
                    int lastSlashIndex = param.LastIndexOf('/');
                    if (lastSlashIndex >= 0)
                    {
                        displayName = param.Substring(lastSlashIndex + 1);
                    }

                    // Ensure display name is not empty
                    if (string.IsNullOrEmpty(displayName))
                    {
                        displayName = param; // Fallback to full parameter name
                    }

                    // Use stored initial selection to avoid accessing disposed property
                    bool isSelected = param == _initialSelectedParameter;
                    GUIStyle style = isSelected ? _selectedItemStyle : _itemStyle;

                    // Draw the button normally
                    if (GUILayout.Button(displayName, style))
                    {
                        SelectParameterSafely(param);
                        return; // Exit since window will close
                    }

                    GUILayout.EndHorizontal();
                    currentY += EditorGUIUtility.singleLineHeight;
                }
                catch (Exception e)
                {
                    Debug.LogError($"Error drawing parameter item '{param ?? "NULL"}': {e.Message}");
                    currentY += EditorGUIUtility.singleLineHeight;
                    // Try to end the horizontal layout if it was started
                    try { GUILayout.EndHorizontal(); } catch { }
                }
            }

            // Handle click outside
            private void OnLostFocus()
            {
                Close();
            }

            // Cleanup on destroy
            private void OnDestroy()
            {
                if (_activeDropdown == this)
                {
                    _activeDropdown = null;
                }
            }
        }

        #endregion

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