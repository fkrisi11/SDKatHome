#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SDKatHome
{
    /// <summary>
    /// The searchable, collapsible tree dropdown used across SDK at Home patches.
    ///
    /// Extracted from the Parameter Driver dropdowns so the Animation window clip picker can share
    /// the same widget. The only behavioural change is how a selection leaves the window: instead
    /// of writing a SerializedProperty directly, it reports the INDEX of the chosen item back
    /// through a callback. Callers keep their own mapping, which is what lets the clip picker
    /// distinguish two clips that happen to share a display name.
    ///
    /// Items are '/'-separated paths. A path segment that is also an item in its own right stays
    /// selectable, so "Hat" and "Hat/On" can both exist.
    /// </summary>
    public class SDKatHomeDropdownWindow : EditorWindow
    {
        // Data
        private string[] _allParameters;
        private Dictionary<string, bool> _categoryExpanded = new Dictionary<string, bool>();

        /// <summary>Receives the index into the items array that was passed to Initialize.</summary>
        private Action<int> _onSelected;

        /// <summary>Item text to its index. First occurrence wins, but callers are expected to
        /// pass unique paths - see the class summary.</summary>
        private Dictionary<string, int> _itemToIndex = new Dictionary<string, int>();

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

        /// <summary>
        /// Populates the dropdown. <paramref name="items"/> are '/'-separated display paths and
        /// should be unique; <paramref name="currentValue"/> is the one to highlight and scroll to;
        /// <paramref name="onSelected"/> receives the index into <paramref name="items"/>.
        /// </summary>
        public void Initialize(string[] items, string currentValue, Action<int> onSelected)
        {
            // Clone before touching it: this method sorts in place, and callers hand us an array
            // whose ORIGINAL order is the index mapping they expect back.
            _allParameters = items != null ? (string[])items.Clone() : new string[0];
            _onSelected = onSelected;

            // Built from the pre-sort order, so a selection resolves to the caller's index.
            _itemToIndex.Clear();
            for (int i = 0; i < _allParameters.Length; i++)
            {
                string item = _allParameters[i];
                if (string.IsNullOrEmpty(item)) continue;
                if (!_itemToIndex.ContainsKey(item)) _itemToIndex[item] = i;
            }

            _selectedParameterPath = currentValue ?? "";
            _initialSelectedParameter = currentValue ?? "";

            _searchText = "";
            _scrollPosition = Vector2.zero;
            _allCategories.Clear();

            _shouldScrollToSelected = !string.IsNullOrEmpty(_selectedParameterPath);

            // Sort parameters (the clone, not the caller's array)
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
                Debug.LogError($"GUI Error in SDKatHomeDropdownWindow: {e.Message}\n{e.StackTrace}");
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

        /// <summary>
        /// Reports the chosen item back to the caller by index and closes. Everything about what a
        /// selection MEANS - writing a property, picking a clip - belongs to the caller's callback.
        /// </summary>
        private void SelectParameterSafely(string param)
        {
            Action<int> callback = _onSelected;

            // Seeded rather than left to TryGetValue: '&&' short-circuits when param is null, so
            // the compiler cannot prove index is assigned on every path.
            int index = -1;
            bool resolved = param != null && _itemToIndex.TryGetValue(param, out index);

            // Close first so the callback can open dialogs or move focus without fighting us, and
            // so a throwing callback cannot leave the dropdown stuck on screen.
            Close();

            if (!resolved)
            {
                Debug.LogWarning($"<color=#00FF00>[SDK at Home]</color> Dropdown: '{param ?? "NULL"}' is not a known item.");
                return;
            }

            if (callback == null) return;

            try
            {
                callback(index);
            }
            catch (Exception e)
            {
                Debug.LogError($"<color=#00FF00>[SDK at Home]</color> Dropdown selection handler failed: {e.Message}");
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
            if (_active == this)
            {
                _active = null;
            }
        }

        #region Presentation

        private static SDKatHomeDropdownWindow _active;

        /// <summary>
        /// Opens the dropdown below <paramref name="buttonRect"/> (GUI space). Any dropdown already
        /// open is closed first. <paramref name="onSelected"/> receives the index into
        /// <paramref name="items"/>, which should contain unique '/'-separated paths.
        /// </summary>
        public static SDKatHomeDropdownWindow Show(
            Rect buttonRect, string[] items, string currentValue, Action<int> onSelected,
            float width = 250f, float height = 300f)
        {
            CloseActive();

            if (items == null || items.Length == 0)
            {
                Debug.LogWarning("<color=#00FF00>[SDK at Home]</color> Dropdown: nothing to show.");
                return null;
            }

            var window = CreateInstance<SDKatHomeDropdownWindow>();
            window.Initialize(items, currentValue, onSelected);

            // ShowPopup rather than ShowAsDropDown: this window handles its own dismissal in
            // OnLostFocus, and ShowAsDropDown would fight it.
            Vector2 screenPos = GUIUtility.GUIToScreenPoint(new Vector2(buttonRect.x, buttonRect.y + buttonRect.height));
            window.position = new Rect(screenPos.x, screenPos.y, Mathf.Max(width, buttonRect.width), height);
            window.ShowPopup();

            _active = window;
            return window;
        }

        /// <summary>Closes the dropdown if one is open. Safe to call when none is. Named
        /// CloseActive rather than Close so it cannot hide EditorWindow.Close() - the instance
        /// calls inside this class must keep resolving to the base method.</summary>
        public static void CloseActive()
        {
            if (_active == null) return;

            try { _active.Close(); }
            catch (Exception) { /* already destroyed */ }
            finally { _active = null; }
        }

        public static bool IsOpen => _active != null;

        #endregion
    }
}
#endif
