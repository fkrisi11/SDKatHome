#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using HarmonyLib;
using System.Reflection;

namespace SDKatHome.Patches
{
    [SDKPatch("Tracking Control - Better UI",
          "Adds alternating row colors and hover effects to VRC Animator Tracking Control",
          "UI Improvements",
          usePrefix: false,
          usePostfix: true)]
    [HarmonyPatch]
    public class TrackingControlStyle
    {
        // Use more subtle colors that work well as overlays
        private static Color alternateColor1 = new Color(1f, 1f, 1f, 0.05f); // Very light overlay
        private static Color alternateColor2 = new Color(0f, 0f, 0f, 0.08f); // Very dark overlay
        private static Color hoverColor = new Color(0.3f, 0.5f, 0.8f, 0.15f); // Blue overlay

        public static MethodBase TargetMethod()
        {
            return typeof(VRCAnimatorTrackingControlEditor).GetMethod("DrawTrackingOption",
                BindingFlags.NonPublic | BindingFlags.Instance);
        }

        [HarmonyPostfix]
        static void Postfix(VRCAnimatorTrackingControlEditor __instance, string name,
            ref VRC.SDKBase.VRC_AnimatorTrackingControl.TrackingType value)
        {
            // Only add coloring for individual rows (not the "All" row)
            if (name != "All")
            {
                Event currentEvent = Event.current;

                // Only draw during repaint events
                if (currentEvent.type == EventType.Repaint)
                {
                    // Get the rect of the last drawn horizontal group
                    Rect lastRect = GUILayoutUtility.GetLastRect();

                    // Create a full-width background rect
                    Rect backgroundRect = new Rect(
                        0, // Start at left edge
                        lastRect.y, // Same Y position as the row
                        EditorGUIUtility.currentViewWidth, // Full width
                        lastRect.height // Same height as the row
                    );

                    int rowIndex = GetRowIndexForName(name);

                    // Determine background color
                    Color bgColor = (rowIndex % 2 == 0) ? alternateColor1 : alternateColor2;

                    // Check for hover
                    if (backgroundRect.Contains(currentEvent.mousePosition))
                    {
                        bgColor = hoverColor;

                        // Request repaint for smooth hover transitions
                        if (currentEvent.type == EventType.MouseMove)
                        {
                            EditorWindow.focusedWindow?.Repaint();
                        }
                    }

                    // Draw the background rectangle as a very subtle overlay
                    // Yes, this draws over the controls, but with very low alpha it creates a nice tint effect
                    EditorGUI.DrawRect(backgroundRect, bgColor);
                }
            }
        }

        private static int GetRowIndexForName(string name)
        {
            switch (name)
            {
                case "Head": return 0;
                case "Left Hand": return 1;
                case "Right Hand": return 2;
                case "Hip": return 3;
                case "Left Foot": return 4;
                case "Right Foot": return 5;
                case "Left Fingers": return 6;
                case "Right Fingers": return 7;
                case "Eyes & Eyelids": return 8;
                case "Mouth & Jaw": return 9;
                default: return 0;
            }
        }
    }
}
#endif