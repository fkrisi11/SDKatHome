#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;

// Attribute to mark patches for auto-registration
[AttributeUsage(AttributeTargets.Class)]
public class SDKPatchAttribute : Attribute
{
    public string Name { get; }
    public string Description { get; }
    public string Category { get; }
    public SDKatHomePatcher.PatchUIType UIType { get; }
    public bool UsePrefix { get; }
    public bool UsePostfix { get; }
    public bool UseTranspiler { get; }
    public bool UseFinalizer { get; }
    public string ButtonText { get; }
    public string ButtonActionMethodName { get; }
    public string[] Options { get; }
    public int DefaultOption { get; }
    public bool[] DefaultSelections { get; }
    public bool EnabledByDefault { get; }

    // Constructor for checkbox patches
    public SDKPatchAttribute(string name, string description, string category = "General",
        bool usePrefix = false, bool usePostfix = true, bool useTranspiler = false, bool useFinalizer = false,
        bool enabledByDefault = true, string buttonText = null, string buttonActionMethodName = null)
    {
        Name = name;
        Description = description;
        Category = category;
        UIType = SDKatHomePatcher.PatchUIType.Checkbox;
        UsePrefix = usePrefix;
        UsePostfix = usePostfix;
        UseTranspiler = useTranspiler;
        UseFinalizer = useFinalizer;
        EnabledByDefault = enabledByDefault;
        ButtonText = buttonText;
        ButtonActionMethodName = buttonActionMethodName;
    }

    // Constructor for single select patches
    public SDKPatchAttribute(string name, string description, string category,
        string[] options, int defaultOption = 0, bool usePrefix = true, bool usePostfix = false,
        bool useTranspiler = false, bool useFinalizer = false, bool enabledByDefault = true,
        string buttonText = null, string buttonActionMethodName = null)
    {
        Name = name;
        Description = description;
        Category = category;
        UIType = SDKatHomePatcher.PatchUIType.SingleSelect;
        Options = options;
        DefaultOption = defaultOption;
        UsePrefix = usePrefix;
        UsePostfix = usePostfix;
        UseTranspiler = useTranspiler;
        UseFinalizer = useFinalizer;
        EnabledByDefault = enabledByDefault;
        ButtonText = buttonText;
        ButtonActionMethodName = buttonActionMethodName;
    }

    // Constructor for multi-select patches
    public SDKPatchAttribute(string name, string description, string category,
        string[] options, bool[] defaultSelections, bool usePrefix = true, bool usePostfix = false,
        bool useTranspiler = false, bool useFinalizer = false, bool enabledByDefault = true,
        string buttonText = null, string buttonActionMethodName = null)
    {
        Name = name;
        Description = description;
        Category = category;
        UIType = SDKatHomePatcher.PatchUIType.MultiSelect;
        Options = options;
        DefaultSelections = defaultSelections;
        UsePrefix = usePrefix;
        UsePostfix = usePostfix;
        UseTranspiler = useTranspiler;
        UseFinalizer = useFinalizer;
        EnabledByDefault = enabledByDefault;
        ButtonText = buttonText;
        ButtonActionMethodName = buttonActionMethodName;
    }
}

[InitializeOnLoad]
public class SDKPatchInitializer
{
    public static string[] AllSDKatHomePreferenceKeys = { };

    static SDKPatchInitializer()
    {
        EditorApplication.delayCall += RegisterPatches;
    }

    private static void RegisterPatches()
    {
        // Auto-discover and register all patches with the SDKPatch attribute
        var patchTypes = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.GetCustomAttribute<SDKPatchAttribute>() != null)
            .ToList();

        int registeredCount = 0;
        var allPreferenceKeys = new List<string>();

        // Add core SDK at Home preference keys
        allPreferenceKeys.AddRange(new string[]
        {
            "SDKatHome_PatchingEnabled"
        });

        foreach (var patchType in patchTypes)
        {
            var attribute = patchType.GetCustomAttribute<SDKPatchAttribute>();

            try
            {
                RegisterPatch(patchType, attribute);

                string patchEnabledKey = "SDKatHome_" + attribute.Name;
                allPreferenceKeys.Add(patchEnabledKey);

                // Try to collect preference keys from this patch
                var patchPreferenceKeys = GetPreferenceKeysFromPatch(patchType);
                if (patchPreferenceKeys != null && patchPreferenceKeys.Length > 0)
                {
                    allPreferenceKeys.AddRange(patchPreferenceKeys);
                }

                registeredCount++;
            }
            catch (Exception ex)
            {
                Debug.LogError($"<color=#00FF00>[SDK at Home]</color> Failed to register patch {patchType.Name}: {ex.Message}");
            }
        }

        AllSDKatHomePreferenceKeys = allPreferenceKeys.ToArray();
        allPreferenceKeys.Clear();

        if (registeredCount == 0)
        {
            Debug.Log($"<color=#00FF00>[SDK at Home]</color> No patches found.");
        }
        else
        {
            Debug.Log($"<color=#00FF00>[SDK at Home]</color> SDK patched.\r\nRegistered patches: {registeredCount}");
        }
    }

    private static string[] GetPreferenceKeysFromPatch(Type patchType)
    {
        try
        {
            // Look for a static GetPreferenceKeys method
            var getPreferenceKeysMethod = patchType.GetMethod("GetPreferenceKeys",
                BindingFlags.Public | BindingFlags.Static,
                null,
                Type.EmptyTypes,
                null);

            if (getPreferenceKeysMethod != null && getPreferenceKeysMethod.ReturnType == typeof(string[]))
            {
                var result = getPreferenceKeysMethod.Invoke(null, null) as string[];
                return result ?? new string[0];
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"<color=#00FF00>[SDK at Home]</color> Failed to get preference keys from {patchType.Name}: {ex.Message}");
        }

        return new string[0];
    }

    private static void RegisterPatch(Type patchType, SDKPatchAttribute attribute)
    {
        // Get the target method from the patch class
        Func<MethodBase> targetMethodFunc = () => GetTargetMethod(patchType);

        if (targetMethodFunc == null)
        {
            Debug.LogError($"Patch {patchType.Name} doesn't have a static TargetMethod() method");
            return;
        }

        // Create button action if specified
        Action buttonAction = null;
        if (!string.IsNullOrEmpty(attribute.ButtonActionMethodName))
        {
            buttonAction = () => InvokeButtonAction(patchType, attribute.ButtonActionMethodName);
        }

        // Register based on UI type
        switch (attribute.UIType)
        {
            case SDKatHomePatcher.PatchUIType.Checkbox:
                SDKatHomePatcher.RegisterCheckboxPatch(
                    targetMethodFunc,
                    patchType,
                    attribute.Name,
                    attribute.Description,
                    attribute.Category,
                    attribute.UsePrefix,
                    attribute.UsePostfix,
                    attribute.UseTranspiler,
                    attribute.UseFinalizer,
                    attribute.EnabledByDefault,
                    attribute.ButtonText,
                    buttonAction
                );
                break;

            case SDKatHomePatcher.PatchUIType.SingleSelect:
                SDKatHomePatcher.RegisterSingleSelectPatch(
                    targetMethodFunc,
                    patchType,
                    attribute.Name,
                    attribute.Options,
                    attribute.DefaultOption,
                    attribute.Description,
                    attribute.Category,
                    attribute.UsePrefix,
                    attribute.UsePostfix,
                    attribute.UseTranspiler,
                    attribute.UseFinalizer,
                    attribute.EnabledByDefault,
                    attribute.ButtonText,
                    buttonAction
                );
                break;

            case SDKatHomePatcher.PatchUIType.MultiSelect:
                SDKatHomePatcher.RegisterMultiSelectPatch(
                    targetMethodFunc,
                    patchType,
                    attribute.Name,
                    attribute.Options,
                    attribute.DefaultSelections,
                    attribute.Description,
                    attribute.Category,
                    attribute.UsePrefix,
                    attribute.UsePostfix,
                    attribute.UseTranspiler,
                    attribute.UseFinalizer,
                    attribute.EnabledByDefault,
                    attribute.ButtonText,
                    buttonAction
                );
                break;
        }
    }

    private static MethodBase GetTargetMethod(Type patchType)
    {
        // Look for a static TargetMethod
        var targetMethod = patchType.GetMethod("TargetMethod", BindingFlags.Public | BindingFlags.Static);
        if (targetMethod != null)
        {
            return (MethodBase)targetMethod.Invoke(null, null);
        }

        return null;
    }

    private static void InvokeButtonAction(Type patchType, string methodName)
    {
        try
        {
            // Check if methodName contains a class specification (e.g., "ClassName.MethodName")
            if (methodName.Contains("."))
            {
                var parts = methodName.Split('.');
                if (parts.Length >= 2)
                {
                    var className = string.Join(".", parts.Take(parts.Length - 1));
                    var actualMethodName = parts.Last();

                    // Find the type by name in all loaded assemblies
                    var targetType = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(assembly => assembly.GetTypes())
                        .FirstOrDefault(type => type.Name == className ||
                                              type.FullName == className ||
                                              type.FullName.EndsWith("." + className));

                    if (targetType != null)
                    {
                        var method = targetType.GetMethod(actualMethodName, BindingFlags.Public | BindingFlags.Static);
                        if (method != null)
                        {
                            method.Invoke(null, null);
                            return;
                        }
                        else
                        {
                            Debug.LogError($"<color=#00FF00>[SDK at Home]</color> Method '{actualMethodName}' not found in class '{className}'");
                            return;
                        }
                    }
                    else
                    {
                        Debug.LogError($"<color=#00FF00>[SDK at Home]</color> Class '{className}' not found");
                        return;
                    }
                }
            }
            else
            {
                // Fallback: look for method in the patch type itself
                var method = patchType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
                if (method != null)
                {
                    method.Invoke(null, null);
                    return;
                }
                else
                {
                    Debug.LogError($"<color=#00FF00>[SDK at Home]</color> Method '{methodName}' not found in patch class '{patchType.Name}'");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"<color=#00FF00>[SDK at Home]</color> Error invoking button action '{methodName}': {ex.Message}\n{ex.StackTrace}");
        }
    }
}
#endif