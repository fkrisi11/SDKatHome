#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;

public abstract class SDKPatchBase
{
    public abstract string PatchName { get; }
    public abstract string Description { get; }
    public virtual string Category => "General";

    public virtual SDKatHomePatcher.PatchUIType UIType => SDKatHomePatcher.PatchUIType.Checkbox;

    public virtual bool UsePrefix => false;
    public virtual bool UsePostfix => true;
    public virtual bool UseTranspiler => false;
    public virtual bool UseFinalizer => false;

    public virtual bool EnabledByDefault => true;

    public virtual string ButtonText => null;
    public virtual string ButtonActionMethodName => null;

    public virtual string[] Options => new string[0];
    public virtual int DefaultOption => 0;
    public virtual bool[] DefaultSelections => new bool[0];
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
                .Where(type => typeof(SDKPatchBase).IsAssignableFrom(type) && !type.IsAbstract)
                .ToList();

        int registeredCount = 0;
        var allPreferenceKeys = new List<string>();

        // Add core SDK at Home preference keys
        allPreferenceKeys.Add("SDKatHome_PatchingEnabled");

        foreach (var patchType in patchTypes)
        {
            try
            {
                var patchInstance = (SDKPatchBase)Activator.CreateInstance(patchType);

                RegisterPatch(patchType, patchInstance);

                string patchEnabledKey = "SDKatHome_" + patchInstance.PatchName;
                allPreferenceKeys.Add(patchEnabledKey);

                var patchPreferenceKeys = GetPreferenceKeysFromPatch(patchType);
                if (patchPreferenceKeys != null)
                    allPreferenceKeys.AddRange(patchPreferenceKeys);

                registeredCount++;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SDK at Home] Failed to register patch {patchType.Name}: {ex.Message}");
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

    private static void RegisterPatch(Type patchType, SDKPatchBase patch)
    {
        // Load enabled state from EditorPrefs (fallback to attribute default)
        string enabledKey = "SDKatHome_" + patch.PatchName;

        // If the key doesn't exist yet, initialize it once so future reloads are stable
        if (!EditorPrefs.HasKey(enabledKey))
            EditorPrefs.SetBool(enabledKey, patch.EnabledByDefault);

        bool enabled = EditorPrefs.GetBool(enabledKey, patch.EnabledByDefault);

        // Get the target method from the patch class
        Func<MethodBase> targetMethodFunc = () => GetTargetMethod(patchType);

        if (targetMethodFunc == null)
        {
            Debug.LogError($"Patch {patchType.Name} doesn't have a static TargetMethod() method");
            return;
        }

        // Create button action if specified
        Action buttonAction = null;
        if (!string.IsNullOrEmpty(patch.ButtonActionMethodName))
        {
            buttonAction = () => InvokeButtonAction(patchType, patch.ButtonActionMethodName);
        }

        // Register based on UI type
        switch (patch.UIType)
        {
            case SDKatHomePatcher.PatchUIType.Checkbox:
                SDKatHomePatcher.RegisterCheckboxPatch(
                    targetMethodFunc,
                    patchType,
                    patch.PatchName,
                    patch.Description,
                    patch.Category,
                    patch.UsePrefix,
                    patch.UsePostfix,
                    patch.UseTranspiler,
                    patch.UseFinalizer,
                    enabled,
                    patch.ButtonText,
                    buttonAction
                );
                break;

            case SDKatHomePatcher.PatchUIType.SingleSelect:
                SDKatHomePatcher.RegisterSingleSelectPatch(
                    targetMethodFunc,
                    patchType,
                    patch.PatchName,
                    patch.Options,
                    patch.DefaultOption,
                    patch.Description,
                    patch.Category,
                    patch.UsePrefix,
                    patch.UsePostfix,
                    patch.UseTranspiler,
                    patch.UseFinalizer,
                    enabled,
                    patch.ButtonText,
                    buttonAction
                );
                break;

            case SDKatHomePatcher.PatchUIType.MultiSelect:
                SDKatHomePatcher.RegisterMultiSelectPatch(
                    targetMethodFunc,
                    patchType,
                    patch.PatchName,
                    patch.Options,
                    patch.DefaultSelections,
                    patch.Description,
                    patch.Category,
                    patch.UsePrefix,
                    patch.UsePostfix,
                    patch.UseTranspiler,
                    patch.UseFinalizer,
                    enabled,
                    patch.ButtonText,
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