#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace SDKatHome
{
    public class AnimatorBackgroundWindow : EditorWindow
    {
        private Vector2 scrollPosition;

        public static void ShowWindow()
        {
            var window = GetWindow<AnimatorBackgroundWindow>("Animator Background");
            window.minSize = new Vector2(400, 650);
            window.Show();
        }

        private void OnEnable()
        {
            if (!Patches.AnimatorBackground.prefsLoaded)
            {
                Patches.AnimatorBackground.LoadPreferences();
                Patches.AnimatorBackground.prefsLoaded = true;
            }
        }

        private void OnGUI()
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            EditorGUILayout.LabelField("Animator Background Settings", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            EditorGUI.BeginChangeCheck();

            // Texture selection
            Patches.AnimatorBackground.overlayTexture = EditorGUILayout.ObjectField("Overlay Texture", Patches.AnimatorBackground.overlayTexture, typeof(Texture2D), false) as Texture2D;

            if (Patches.AnimatorBackground.overlayTexture != null)
            {
                EditorGUILayout.Space();

                // Basic settings
                Patches.AnimatorBackground.overlayOpacity = EditorGUILayout.Slider("Opacity", Patches.AnimatorBackground.overlayOpacity, 0f, 1f);
                Patches.AnimatorBackground.overlayTint = EditorGUILayout.ColorField("Tint Color", Patches.AnimatorBackground.overlayTint);

                EditorGUILayout.Space();

                Patches.AnimatorBackground.overlayScaleMode = (ScaleMode)EditorGUILayout.EnumPopup("Scale Mode", Patches.AnimatorBackground.overlayScaleMode);
                Patches.AnimatorBackground.overlayTiling = EditorGUILayout.Toggle("Enable Tiling", Patches.AnimatorBackground.overlayTiling);

                EditorGUILayout.Space();

                Patches.AnimatorBackground.overlayScale = EditorGUILayout.Vector2Field("Scale", Patches.AnimatorBackground.overlayScale);
                Patches.AnimatorBackground.overlayOffset = EditorGUILayout.Vector2Field("Offset", Patches.AnimatorBackground.overlayOffset);

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Parallax Settings", EditorStyles.boldLabel);

                Patches.AnimatorBackground.enableParallax = EditorGUILayout.Toggle("Enable Parallax", Patches.AnimatorBackground.enableParallax);

                if (Patches.AnimatorBackground.enableParallax)
                {
                    EditorGUILayout.HelpBox("This causes the animator window to constantly be redrawn, which can cause performance issues", MessageType.Warning);
                }

                using (new EditorGUI.DisabledScope(!Patches.AnimatorBackground.enableParallax))
                {
                    EditorGUI.indentLevel++;

                    Patches.AnimatorBackground.parallaxStrength = EditorGUILayout.Slider("Parallax Strength", Patches.AnimatorBackground.parallaxStrength, 0f, 1f);

                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Direction Control:", EditorStyles.miniBoldLabel);
                    Patches.AnimatorBackground.invertParallaxX = EditorGUILayout.Toggle("Invert X Movement", Patches.AnimatorBackground.invertParallaxX);
                    Patches.AnimatorBackground.invertParallaxY = EditorGUILayout.Toggle("Invert Y Movement", Patches.AnimatorBackground.invertParallaxY);

                    EditorGUILayout.Space();
                    Patches.AnimatorBackground.parallaxSmoothing = EditorGUILayout.Slider("Smoothing", Patches.AnimatorBackground.parallaxSmoothing, 0f, 10f);

                    EditorGUI.indentLevel--;

                    EditorGUILayout.HelpBox("Parallax creates a subtle background movement effect that follows your mouse cursor. " +
                                          "Higher strength values create more dramatic movement. Smoothing controls how quickly the effect responds to mouse movement (0 = instant, 10 = very smooth and floaty).",
                                          MessageType.Info);
                }

                EditorGUILayout.Space();
            }
            else
            {
                EditorGUILayout.HelpBox("Select a texture to use as overlay", MessageType.Info);
            }

            if (EditorGUI.EndChangeCheck())
            {
                Patches.AnimatorBackground.SavePreferences();
            }

            EditorGUILayout.Space();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reset to Defaults"))
                {
                    ResetToDefaults();
                }

                if (GUILayout.Button("Reset Parallax"))
                {
                    ResetParallaxDefaults();
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.EndScrollView();
        }

        private void ResetToDefaults()
        {
            Patches.AnimatorBackground.overlayTexture = null;
            Patches.AnimatorBackground.overlayOpacity = 0.1f;
            Patches.AnimatorBackground.overlayScale = Vector2.one;
            Patches.AnimatorBackground.overlayOffset = Vector2.zero;
            Patches.AnimatorBackground.overlayScaleMode = ScaleMode.ScaleToFit;
            Patches.AnimatorBackground.overlayTiling = false;
            Patches.AnimatorBackground.overlayTint = Color.white;
            ResetParallaxDefaults();
            Patches.AnimatorBackground.SavePreferences();
            Repaint();
        }

        private void ResetParallaxDefaults()
        {
            Patches.AnimatorBackground.enableParallax = false;
            Patches.AnimatorBackground.parallaxStrength = 0.025f;
            Patches.AnimatorBackground.invertParallaxX = false;
            Patches.AnimatorBackground.invertParallaxY = false;
            Patches.AnimatorBackground.parallaxSmoothing = 1f;

            // Reset runtime parallax state
            Patches.AnimatorBackground.currentParallaxOffset = Vector2.zero;
            Patches.AnimatorBackground.targetParallaxOffset = Vector2.zero;
            Patches.AnimatorBackground.hasInitializedMouse = false;

            Patches.AnimatorBackground.SavePreferences();
            Repaint();
        }
    }
}
#endif