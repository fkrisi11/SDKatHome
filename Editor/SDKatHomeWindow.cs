#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public class SDKatHomeWindow : EditorWindow
{
    private Vector2 _scrollPosition;
    private bool _patchingEnabled = true;
    private GUIStyle _titleStyle;
    private GUIStyle _categoryStyle;
    private GUIStyle _descriptionStyle;

    // Foldout states for categories
    private Dictionary<string, bool> _categoryFoldouts = new Dictionary<string, bool>();

    [MenuItem("TohruTheDragon/SDK at Home")]
    public static void ShowWindow()
    {
        GetWindow<SDKatHomeWindow>("SDK at Home");
    }

    private void OnEnable()
    {
        _patchingEnabled = EditorPrefs.GetBool("SDKatHome_PatchingEnabled", true);
        EditorApplication.delayCall += () => {
            LoadPatchSettings();
            Repaint();
        };
    }

    private void OnGUI()
    {
        SetupStyles();
        DrawHeader();

        var patchesByCategory = SDKatHomePatcher.GetPatchesByCategory();
        if (patchesByCategory.Count == 0)
        {
            EditorGUILayout.HelpBox("No patches available.", MessageType.Info);
            return;
        }

        _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

        // Save current GUI enabled state
        bool originalGUIState = GUI.enabled;
        GUI.enabled = _patchingEnabled;

        // Draw patches organized by category
        foreach (var category in patchesByCategory.Keys.OrderBy(x => x))
        {
            DrawCategorySection(category, patchesByCategory[category]);
        }

        GUI.enabled = originalGUIState;
        EditorGUILayout.EndScrollView();

        Color og = GUI.backgroundColor;
        GUI.backgroundColor = new Color(0.7f, 0.2f, 0.2f);
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("", _titleStyle);

        if (GUILayout.Button("Clear preferences", GUILayout.Width(120)))
        {
            if (EditorUtility.DisplayDialog("Clear SDK at Home Preferences",
                                            "This will clear all SDK at Home preferences including:\n\n" +
                                            "• Patching enabled state\n" +
                                            "• Individual patch settings\n" +
                                            "• Category foldout states\n" +
                                            "• Single/Multi-select options\n\n" +
                                            "Are you sure you want to continue?",
                                            "Clear All", "Cancel"))
            {
                ClearPreferences();
            }
        }

        EditorGUILayout.EndHorizontal();
        GUI.backgroundColor = og;
    }

    private void SetupStyles()
    {
        if (_titleStyle == null)
        {
            _titleStyle = new GUIStyle(EditorStyles.boldLabel);
            _titleStyle.fontSize = 16;
            _titleStyle.margin.bottom = 10;
        }

        if (_categoryStyle == null)
        {
            _categoryStyle = new GUIStyle(EditorStyles.foldout);
            _categoryStyle.fontStyle = FontStyle.Bold;
            _categoryStyle.fontSize = 12;
        }

        if (_descriptionStyle == null)
        {
            _descriptionStyle = new GUIStyle(EditorStyles.helpBox);
            _descriptionStyle.fontSize = 10;
            _descriptionStyle.wordWrap = true;
        }
    }

    private void DrawHeader()
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("SDK at Home", _titleStyle);

        Color originalColor = GUI.backgroundColor;
        GUI.backgroundColor = _patchingEnabled ?
            new Color(0.2f, 0.7f, 0.2f) : new Color(0.7f, 0.2f, 0.2f);

        if (GUILayout.Button(_patchingEnabled ? "Enabled" : "Disabled", GUILayout.Width(120)))
        {
            _patchingEnabled = !_patchingEnabled;
            ApplyGlobalPatchingState();
            EditorPrefs.SetBool("SDKatHome_PatchingEnabled", _patchingEnabled);
        }

        GUI.backgroundColor = originalColor;
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.Space();
    }

    private void DrawCategorySection(string category, List<string> patchNames)
    {
        // Get or set foldout state
        if (!_categoryFoldouts.ContainsKey(category))
        {
            _categoryFoldouts[category] = EditorPrefs.GetBool($"SDKatHome_Category_{category}", true);
        }

        bool foldoutState = EditorGUILayout.Foldout(_categoryFoldouts[category], category, _categoryStyle);

        if (foldoutState != _categoryFoldouts[category])
        {
            _categoryFoldouts[category] = foldoutState;
            EditorPrefs.SetBool($"SDKatHome_Category_{category}", foldoutState);
        }

        if (!foldoutState) return;

        EditorGUI.indentLevel++;

        foreach (string patchName in patchNames)
        {
            DrawPatchControl(patchName);
        }

        EditorGUI.indentLevel--;
        EditorGUILayout.Space(5);
    }

    private void DrawPatchControl(string patchName)
    {
        var patchInfo = SDKatHomePatcher.GetPatchInfo(patchName);

        EditorGUILayout.BeginVertical("box");

        switch (patchInfo.UIType)
        {
            case SDKatHomePatcher.PatchUIType.Checkbox:
                DrawCheckboxPatch(patchName, patchInfo);
                break;

            case SDKatHomePatcher.PatchUIType.SingleSelect:
                DrawSingleSelectPatch(patchName, patchInfo);
                break;

            case SDKatHomePatcher.PatchUIType.MultiSelect:
                DrawMultiSelectPatch(patchName, patchInfo);
                break;
        }

        // Draw optional button
        if (patchInfo.HasButton)
        {
            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace(); // Push button to the right

            if (GUILayout.Button(patchInfo.ButtonText, EditorStyles.miniButton, GUILayout.Width(100)))
            {
                try
                {
                    patchInfo.ButtonAction?.Invoke();
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"<color=#00FF00>[SDK at Home]</color> Error executing button action for {patchName}: {e.Message}");
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        // Draw description if available
        if (!string.IsNullOrEmpty(patchInfo.Description))
        {
            EditorGUILayout.LabelField(patchInfo.Description, _descriptionStyle);
        }

        EditorGUILayout.EndVertical();
        EditorGUILayout.Space(2);
    }

    private void DrawCheckboxPatch(string patchName, SDKatHomePatcher.PatchInfo patchInfo)
    {
        bool isConfiguredActive = EditorPrefs.GetBool("SDKatHome_" + patchName, patchInfo.EnabledByDefault);

        EditorGUILayout.BeginHorizontal();

        // Give more space for the patch name and make the checkbox larger/easier to click
        EditorGUILayout.LabelField(patchName, GUILayout.ExpandWidth(true));

        EditorGUI.BeginChangeCheck();
        // Make checkbox wider and easier to click
        bool newConfiguredState = EditorGUILayout.Toggle(isConfiguredActive, GUILayout.Width(30));
        bool changed = EditorGUI.EndChangeCheck();

        EditorGUILayout.EndHorizontal();

        if (changed && newConfiguredState != isConfiguredActive)
        {
            SavePatchSetting(patchName, newConfiguredState);

            if (_patchingEnabled)
            {
                if (newConfiguredState)
                {
                    SDKatHomePatcher.ApplyPatch(patchName);
                }
                else
                {
                    SDKatHomePatcher.RemovePatch(patchName);
                }
            }
        }
    }

    private void DrawSingleSelectPatch(string patchName, SDKatHomePatcher.PatchInfo patchInfo)
    {
        bool isPatchEnabled = EditorPrefs.GetBool("SDKatHome_" + patchName, patchInfo.EnabledByDefault);
        int currentSelection = EditorPrefs.GetInt($"SDKatHome_{patchName}_Selection", patchInfo.SelectedOption);

        EditorGUILayout.BeginHorizontal();

        // Give more space for the patch name and make the checkbox larger/easier to click
        EditorGUILayout.LabelField(patchName, GUILayout.ExpandWidth(true));

        EditorGUI.BeginChangeCheck();
        // Make checkbox wider and easier to click
        bool newEnabledState = EditorGUILayout.Toggle(isPatchEnabled, GUILayout.Width(30));
        bool enabledChanged = EditorGUI.EndChangeCheck();

        EditorGUILayout.EndHorizontal();

        // Dropdown for options (always show, but gray out if disabled)
        EditorGUI.indentLevel++;

        // Gray out the dropdown if patch is disabled or global patching is disabled
        bool originalGUIEnabled = GUI.enabled;
        if (!isPatchEnabled && !newEnabledState)
        {
            GUI.enabled = false;
        }

        EditorGUI.BeginChangeCheck();
        int newSelection = EditorGUILayout.Popup("Option:", currentSelection, patchInfo.Options);
        bool selectionChanged = EditorGUI.EndChangeCheck();

        // Restore GUI state
        GUI.enabled = originalGUIEnabled;

        EditorGUI.indentLevel--;

        if (selectionChanged)
        {
            //Debug.Log($"<color=#00FF00>[SDK at Home]</color> Selection changed for {patchName}: {currentSelection} -> {newSelection}");
            // Always update selection, regardless of enabled state
            SDKatHomePatcher.UpdatePatchSelection(patchName, newSelection);
        }

        if (enabledChanged)
        {
            //Debug.Log($"<color=#00FF00>[SDK at Home]</color> Enabled state changed for {patchName}: {isPatchEnabled} -> {newEnabledState}");
            SavePatchSetting(patchName, newEnabledState);

            if (_patchingEnabled)
            {
                if (newEnabledState)
                {
                    SDKatHomePatcher.ApplyPatch(patchName);
                }
                else
                {
                    SDKatHomePatcher.RemovePatch(patchName);
                }
            }
        }
    }

    private void DrawMultiSelectPatch(string patchName, SDKatHomePatcher.PatchInfo patchInfo)
    {
        bool isPatchEnabled = EditorPrefs.GetBool("SDKatHome_" + patchName, patchInfo.EnabledByDefault);

        EditorGUILayout.BeginHorizontal();

        // Give more space for the patch name and make the checkbox larger/easier to click
        EditorGUILayout.LabelField(patchName, GUILayout.ExpandWidth(true));

        EditorGUI.BeginChangeCheck();
        // Make checkbox wider and easier to click
        bool newEnabledState = EditorGUILayout.Toggle(isPatchEnabled, GUILayout.Width(30));
        bool enabledChanged = EditorGUI.EndChangeCheck();

        EditorGUILayout.EndHorizontal();

        // Multi-select options (always show, but gray out if disabled)
        EditorGUI.indentLevel++;

        // Gray out the options if patch is disabled
        bool originalGUIEnabled = GUI.enabled;
        if (!isPatchEnabled && !newEnabledState)
        {
            GUI.enabled = false;
        }

        bool[] currentSelections = new bool[patchInfo.Options.Length];
        bool anyChanged = false;

        for (int i = 0; i < patchInfo.Options.Length; i++)
        {
            bool currentState = EditorPrefs.GetBool($"SDKatHome_{patchName}_Option_{i}",
                patchInfo.SelectedOptions != null ? patchInfo.SelectedOptions[i] : false);

            EditorGUI.BeginChangeCheck();
            bool newState = EditorGUILayout.ToggleLeft(patchInfo.Options[i], currentState);

            if (EditorGUI.EndChangeCheck())
            {
                anyChanged = true;
            }

            currentSelections[i] = newState;
        }

        // Restore GUI state
        GUI.enabled = originalGUIEnabled;

        if (anyChanged)
        {
            Debug.Log($"<color=#00FF00>[SDK at Home]</color> Multi-selection changed for {patchName}");
            // Always update selection, regardless of enabled state
            SDKatHomePatcher.UpdatePatchMultiSelection(patchName, currentSelections);
        }

        EditorGUI.indentLevel--;

        if (enabledChanged)
        {
            Debug.Log($"<color=#00FF00>[SDK at Home]</color> Enabled state changed for {patchName}: {isPatchEnabled} -> {newEnabledState}");
            SavePatchSetting(patchName, newEnabledState);

            if (_patchingEnabled)
            {
                if (newEnabledState)
                {
                    SDKatHomePatcher.ApplyPatch(patchName);
                }
                else
                {
                    SDKatHomePatcher.RemovePatch(patchName);
                }
            }
        }
    }

    private void SavePatchSetting(string patchName, bool enabled)
    {
        EditorPrefs.SetBool("SDKatHome_" + patchName, enabled);
    }

    private void LoadPatchSettings()
    {
        var patchNames = SDKatHomePatcher.GetAllPatchNames();

        foreach (string patchName in patchNames)
        {
            var patchInfo = SDKatHomePatcher.GetPatchInfo(patchName);

            bool shouldBeActive = EditorPrefs.GetBool("SDKatHome_" + patchName, patchInfo.EnabledByDefault);

            // Load saved selections for dropdown patches
            if (patchInfo.UIType == SDKatHomePatcher.PatchUIType.SingleSelect)
            {
                int savedSelection = EditorPrefs.GetInt($"SDKatHome_{patchName}_Selection", patchInfo.SelectedOption);
                SDKatHomePatcher.UpdatePatchSelection(patchName, savedSelection);
            }
            else if (patchInfo.UIType == SDKatHomePatcher.PatchUIType.MultiSelect && patchInfo.Options != null)
            {
                bool[] savedSelections = new bool[patchInfo.Options.Length];
                for (int i = 0; i < patchInfo.Options.Length; i++)
                {
                    savedSelections[i] = EditorPrefs.GetBool($"SDKatHome_{patchName}_Option_{i}",
                        patchInfo.SelectedOptions != null ? patchInfo.SelectedOptions[i] : false);
                }
                SDKatHomePatcher.UpdatePatchMultiSelection(patchName, savedSelections);
            }

            if (_patchingEnabled && shouldBeActive)
            {
                SDKatHomePatcher.ApplyPatch(patchName);
            }
            else if (SDKatHomePatcher.IsPatchActive(patchName))
            {
                SDKatHomePatcher.RemovePatch(patchName);
            }
        }
    }

    private void ApplyGlobalPatchingState()
    {
        var patchNames = SDKatHomePatcher.GetAllPatchNames();

        if (_patchingEnabled)
        {
            foreach (string patchName in patchNames)
            {
                var patchInfo = SDKatHomePatcher.GetPatchInfo(patchName);
                bool shouldBeActive = EditorPrefs.GetBool("SDKatHome_" + patchName, patchInfo.EnabledByDefault);

                if (shouldBeActive)
                {
                    SDKatHomePatcher.ApplyPatch(patchName);
                }
            }
        }
        else
        {
            SDKatHomePatcher.UnpatchAll();
        }
    }

    private static void ClearPreferences()
    {
        // Clear all known SDK at Home preference keys
        foreach (string key in SDKPatchInitializer.AllSDKatHomePreferenceKeys)
        {
            if (EditorPrefs.HasKey(key))
            {
                EditorPrefs.DeleteKey(key);
            }
        }

        Debug.Log($"<color=#00FF00>[SDK at Home]</color> Cleared SDK at Home preferences. Please restart Unity for changes to take effect.");
        EditorUtility.DisplayDialog("Preferences Cleared",
            $"Successfully cleared SDK at Home preferences.\n\n" +
            "Please restart Unity for changes to take effect.",
            "OK");
    }

}
#endif