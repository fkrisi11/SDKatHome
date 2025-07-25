#if UNITY_EDITOR
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEditor;

[InitializeOnLoad]
public class SDKatHomePatcher
{
    private static Harmony _harmony;
    private static readonly string PatchId = "com.tohruthedragon.sdkathome";
    private static readonly Dictionary<string, PatchInfo> _patches = new Dictionary<string, PatchInfo>();

    // Enhanced patch types
    public enum PatchUIType
    {
        Checkbox,           // Simple on/off toggle
        SingleSelect,       // Choose one option from multiple variants
        MultiSelect         // Choose multiple options from a list
    }

    // Enhanced patch info structure
    public struct PatchInfo
    {
        public MethodBase TargetMethod;
        public Type PatchClass;
        public bool UsePrefix;
        public bool UsePostfix;
        public bool UseTranspiler;
        public bool UseFinalizer;
        public bool IsActive;

        // New UI-related fields
        public PatchUIType UIType;
        public string[] Options;           // For dropdowns
        public int SelectedOption;         // For single select
        public bool[] SelectedOptions;     // For multi-select
        public string Description;         // Tooltip/help text
        public string Category;            // For grouping patches

        public bool HasButton;             // Whether this patch has a button
        public string ButtonText;          // Text to display on the button
        public Action ButtonAction; // Action to execute when button is clicked

        public bool EnabledByDefault;
    }

    static SDKatHomePatcher()
    {
        _harmony = new Harmony(PatchId);
        EditorApplication.delayCall += Initialize;
    }

    private static void Initialize()
    {
        // Register your patches here or call from another class
    }

    #region Registration Methods

    /// <summary>
    /// Register a simple checkbox patch
    /// </summary>
    public static void RegisterCheckboxPatch(
        Func<MethodBase> targetMethodFinder,
        Type patchClass,
        string patchName,
        string description = "",
        string category = "General",
        bool usePrefix = true,
        bool usePostfix = false,
        bool useTranspiler = false,
        bool useFinalizer = false,
        bool enabled = true,
        string buttonText = null,
        Action buttonAction = null)
    {
        RegisterPatch(targetMethodFinder, patchClass, patchName, PatchUIType.Checkbox,
            null, description, category, usePrefix, usePostfix, useTranspiler, useFinalizer, enabled, 0, null, buttonText, buttonAction);
    }

    /// <summary>
    /// Register a single-select dropdown patch with multiple variants
    /// </summary>
    public static void RegisterSingleSelectPatch(
        Func<MethodBase> targetMethodFinder,
        Type patchClass,
        string patchName,
        string[] options,
        int defaultOption = 0,
        string description = "",
        string category = "General",
        bool usePrefix = true,
        bool usePostfix = false,
        bool useTranspiler = false,
        bool useFinalizer = false,
        bool enabled = true,
        string buttonText = null,
        Action buttonAction = null)
    {
        RegisterPatch(targetMethodFinder, patchClass, patchName, PatchUIType.SingleSelect,
            options, description, category, usePrefix, usePostfix, useTranspiler, useFinalizer, enabled, defaultOption, null, buttonText, buttonAction);
    }

    /// <summary>
    /// Register a multi-select dropdown patch
    /// </summary>
    public static void RegisterMultiSelectPatch(
        Func<MethodBase> targetMethodFinder,
        Type patchClass,
        string patchName,
        string[] options,
        bool[] defaultSelections = null,
        string description = "",
        string category = "General",
        bool usePrefix = true,
        bool usePostfix = false,
        bool useTranspiler = false,
        bool useFinalizer = false,
        bool enabled = true,
        string buttonText = null,
        Action buttonAction = null)
    {
        RegisterPatch(targetMethodFinder, patchClass, patchName, PatchUIType.MultiSelect,
            options, description, category, usePrefix, usePostfix, useTranspiler, useFinalizer, enabled, 0, defaultSelections, buttonText, buttonAction);
    }

    /// <summary>
    /// Core registration method
    /// </summary>
    private static void RegisterPatch(
        Func<MethodBase> targetMethodFinder,
        Type patchClass,
        string patchName,
        PatchUIType uiType,
        string[] options = null,
        string description = "",
        string category = "General",
        bool usePrefix = true,
        bool usePostfix = false,
        bool useTranspiler = false,
        bool useFinalizer = false,
        bool enabled = true,
        int defaultOption = 0,
        bool[] defaultSelections = null,
        string buttonText = null,
        Action buttonAction = null)
    {
        try
        {
            MethodBase targetMethod = targetMethodFinder();
            if (targetMethod == null)
            {
                Debug.LogError($"<color=#00FF00>[SDK@Home]</color> Failed to find target method for {patchName}");
                return;
            }

            var patchInfo = new PatchInfo
            {
                TargetMethod = targetMethod,
                PatchClass = patchClass,
                UsePrefix = usePrefix,
                UsePostfix = usePostfix,
                UseTranspiler = useTranspiler,
                UseFinalizer = useFinalizer,
                IsActive = false,
                UIType = uiType,
                Options = options,
                SelectedOption = defaultOption,
                SelectedOptions = defaultSelections ?? (options != null ? new bool[options.Length] : null),
                Description = description,
                Category = category,
                HasButton = !string.IsNullOrEmpty(buttonText) && buttonAction != null,
                ButtonText = buttonText ?? "",
                ButtonAction = buttonAction,
                EnabledByDefault = enabled
            };

            _patches[patchName] = patchInfo;

            if (enabled)
            {
                ApplyPatch(patchName);
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"<color=#00FF00>[SDK@Home]</color> Failed to register {patchName}: {e.Message}");
        }
    }

    #endregion

    #region Patch Management

    /// <summary>
    /// Apply a registered patch by name
    /// </summary>
    public static bool ApplyPatch(string patchName)
    {
        if (!_patches.TryGetValue(patchName, out PatchInfo patchInfo))
        {
            Debug.LogError($"<color=#00FF00>[SDK@Home]</color> Cannot apply unknown patch: {patchName}");
            return false;
        }

        if (patchInfo.IsActive)
        {
            // If already active, remove it first to reapply with new settings
            RemovePatch(patchName);
            patchInfo = _patches[patchName]; // Get updated info after removal
        }

        try
        {
            // Load current configuration from EditorPrefs before applying
            LoadPatchConfiguration(patchName, ref patchInfo);

            // Set up each patch type if requested
            HarmonyMethod prefix = patchInfo.UsePrefix ? CreatePatchMethod(patchInfo.PatchClass, "Prefix", patchName) : null;
            HarmonyMethod postfix = patchInfo.UsePostfix ? CreatePatchMethod(patchInfo.PatchClass, "Postfix", patchName) : null;
            HarmonyMethod transpiler = patchInfo.UseTranspiler ? CreatePatchMethod(patchInfo.PatchClass, "Transpiler", patchName) : null;
            HarmonyMethod finalizer = patchInfo.UseFinalizer ? CreatePatchMethod(patchInfo.PatchClass, "Finalizer", patchName) : null;

            // Apply the patch
            _harmony.Patch(patchInfo.TargetMethod, prefix, postfix, transpiler, finalizer);

            // Update status
            patchInfo.IsActive = true;
            _patches[patchName] = patchInfo;

            //Debug.Log($"<color=#00FF00>[SDK@Home]</color> Applied patch '{patchName}' with config: " +
            //         $"Selected={patchInfo.SelectedOption}, Options={string.Join(",", patchInfo.SelectedOptions ?? new bool[0])}");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to apply patch {patchName}: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Load patch configuration from EditorPrefs
    /// </summary>
    private static void LoadPatchConfiguration(string patchName, ref PatchInfo patchInfo)
    {
        // Load single-select option
        if (patchInfo.UIType == PatchUIType.SingleSelect)
        {
            patchInfo.SelectedOption = EditorPrefs.GetInt($"SDKatHome_{patchName}_Selection", patchInfo.SelectedOption);
        }

        // Load multi-select options
        if (patchInfo.UIType == PatchUIType.MultiSelect && patchInfo.Options != null)
        {
            if (patchInfo.SelectedOptions == null)
            {
                patchInfo.SelectedOptions = new bool[patchInfo.Options.Length];
            }

            for (int i = 0; i < patchInfo.Options.Length && i < patchInfo.SelectedOptions.Length; i++)
            {
                patchInfo.SelectedOptions[i] = EditorPrefs.GetBool($"SDKatHome_{patchName}_Option_{i}",
                    patchInfo.SelectedOptions[i]);
            }
        }

        // Update the patches dictionary
        _patches[patchName] = patchInfo;
    }

    /// <summary>
    /// Remove a specific patch by name
    /// </summary>
    public static bool RemovePatch(string patchName)
    {
        if (!_patches.TryGetValue(patchName, out PatchInfo patchInfo) || !patchInfo.IsActive)
        {
            Debug.LogWarning($"<color=#00FF00>[SDK@Home]</color> Cannot remove inactive or unknown patch: {patchName}");
            return false;
        }

        try
        {
            _harmony.Unpatch(patchInfo.TargetMethod, HarmonyPatchType.All, PatchId);
            patchInfo.IsActive = false;
            _patches[patchName] = patchInfo;
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"<color=#00FF00>[SDK@Home]</color> Failed to remove patch {patchName}: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Update patch selection (for single-select dropdowns)
    /// </summary>
    public static bool UpdatePatchSelection(string patchName, int selectedOption)
    {
        if (!_patches.TryGetValue(patchName, out PatchInfo patchInfo))
        {
            Debug.LogWarning($"<color=#00FF00>[SDK@Home]</color> Cannot update unknown patch: {patchName}");
            return false;
        }

        if (patchInfo.UIType != PatchUIType.SingleSelect)
        {
            Debug.LogWarning($"<color=#00FF00>[SDK@Home]</color> Patch {patchName} is not a single-select patch");
            return false;
        }

        // Always save to EditorPrefs first
        EditorPrefs.SetInt($"SDKatHome_{patchName}_Selection", selectedOption);

        // Update in-memory configuration
        patchInfo.SelectedOption = selectedOption;
        _patches[patchName] = patchInfo;

        // If patch is currently active, reapply it with new configuration
        if (patchInfo.IsActive)
        {
            //Debug.Log($"<color=#00FF00>[SDK@Home]</color> Reapplying active patch {patchName} with new configuration");
            return ApplyPatch(patchName);
        }

        return true;
    }

    /// <summary>
    /// Update patch multi-selection (for multi-select dropdowns)
    /// </summary>
    public static bool UpdatePatchMultiSelection(string patchName, bool[] selectedOptions)
    {
        if (!_patches.TryGetValue(patchName, out PatchInfo patchInfo))
        {
            Debug.LogWarning($"<color=#00FF00>[SDK@Home]</color> Cannot update unknown patch: {patchName}");
            return false;
        }

        if (patchInfo.UIType != PatchUIType.MultiSelect)
        {
            Debug.LogWarning($"<color=#00FF00>[SDK@Home]</color> Patch {patchName} is not a multi-select patch");
            return false;
        }

        // Always save to EditorPrefs first
        for (int i = 0; i < selectedOptions.Length; i++)
        {
            EditorPrefs.SetBool($"SDKatHome_{patchName}_Option_{i}", selectedOptions[i]);
        }

        // Update in-memory configuration
        patchInfo.SelectedOptions = selectedOptions;
        _patches[patchName] = patchInfo;

        Debug.Log($"<color=#00FF00>[SDK@Home]</color> Updated {patchName} multi-selection: {string.Join(",", selectedOptions)}");

        // If patch is currently active, reapply it with new configuration
        if (patchInfo.IsActive)
        {
            Debug.Log($"<color=#00FF00>[SDK@Home]</color> Reapplying active patch {patchName} with new multi-selection");
            return ApplyPatch(patchName);
        }

        return true;
    }

    #endregion

    #region Getters

    public static PatchInfo GetPatchInfo(string patchName)
    {
        _patches.TryGetValue(patchName, out PatchInfo patchInfo);
        return patchInfo;
    }

    public static bool IsPatchActive(string patchName)
    {
        return _patches.TryGetValue(patchName, out PatchInfo patchInfo) && patchInfo.IsActive;
    }

    public static List<string> GetAllPatchNames()
    {
        return new List<string>(_patches.Keys);
    }

    public static Dictionary<string, List<string>> GetPatchesByCategory()
    {
        var categorized = new Dictionary<string, List<string>>();

        foreach (var kvp in _patches)
        {
            string category = kvp.Value.Category;
            if (!categorized.ContainsKey(category))
            {
                categorized[category] = new List<string>();
            }
            categorized[category].Add(kvp.Key);
        }

        return categorized;
    }

    #endregion

    #region Helper Methods

    private static HarmonyMethod CreatePatchMethod(Type patchClass, string methodName, string patchName)
    {
        MethodInfo method = patchClass.GetMethod(methodName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        if (method == null)
            Debug.LogWarning($"<color=#00FF00>[SDK@Home]</color> Could not find {methodName} method in {patchName}");

        return method != null ? new HarmonyMethod(method) : null;
    }

    public static void UnpatchAll()
    {
        _harmony?.UnpatchAll(PatchId);

        List<string> patchNames = new List<string>(_patches.Keys);
        foreach (string patchName in patchNames)
        {
            PatchInfo patchInfo = _patches[patchName];
            patchInfo.IsActive = false;
            _patches[patchName] = patchInfo;
        }
    }

    #endregion
}
#endif