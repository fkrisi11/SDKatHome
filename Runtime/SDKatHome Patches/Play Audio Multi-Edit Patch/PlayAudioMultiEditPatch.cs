#if UNITY_EDITOR
using UnityEditor;
using HarmonyLib;
using System.Reflection;

namespace SDKatHome.Patches
{
    [SDKPatch("PlayAudio Multi-Edit Support",
          "Enables Unity's native multi-editing capabilities for VRC Animator Play Audio components",
          "Editor Improvements",
          usePrefix: true,
          usePostfix: false,
          buttonText: "Open Editor",
          buttonActionMethodName: "SDKatHome.PlayAudioMultiEditWindow.ShowWindow")]
    [HarmonyPatch]
    public class PlayAudioMultiEditPatch
    {
        public static MethodBase TargetMethod()
        {
            // Target the CustomEditor's OnInspectorGUI method
            return typeof(VRC_AnimatorPlayAudioEditor).GetMethod("OnInspectorGUI",
                BindingFlags.Public | BindingFlags.Instance);
        }

        [HarmonyPrefix]
        static bool Prefix(VRC_AnimatorPlayAudioEditor __instance)
        {
            // Check if we're in multi-edit mode
            if (__instance.targets.Length > 1)
            {
                // Draw a simple message directing users to the multi-edit window
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox($"Multi-editing {__instance.targets.Length} VRC Animator Play Audio components", MessageType.Info);

                EditorGUILayout.Space();
                EditorGUILayout.HelpBox("Use the PlayAudio Multi-Editor window above for proper multi-editing support, or select individual components to edit them normally.", MessageType.Info);

                return false; // Skip the original method
            }

            return true; // Allow original method to run for single selection
        }
    }
}
#endif