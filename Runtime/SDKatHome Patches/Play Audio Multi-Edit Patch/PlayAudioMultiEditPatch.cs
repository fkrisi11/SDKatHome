#if UNITY_EDITOR
using UnityEditor;
using HarmonyLib;
using System.Reflection;

namespace SDKatHome.Patches
{
    [HarmonyPatch]
    public class PlayAudioMultiEditPatch : SDKPatchBase
    {
        public override string PatchName => "PlayAudio Multi-Edit Support";
        public override string Description => "Enables Unity's native multi-editing capabilities for VRC Animator Play Audio components";
        public override string Category => "Editor Improvements";
        public override bool UsePrefix => true;
        public override bool UsePostfix => false;
        public override string ButtonText => "Open Editor";
        public override string ButtonActionMethodName => "SDKatHome.PlayAudioMultiEditWindow.ShowWindow";

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