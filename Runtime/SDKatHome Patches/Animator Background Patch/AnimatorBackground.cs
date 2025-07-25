#if UNITY_EDITOR
using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace SDKatHome.Patches
{
    [SDKPatch("Animator Background",
              "Adds customizable image overlay behind the Unity animator grid with optional parallax scrolling",
              "Animator Tools",
              usePrefix: false,
              usePostfix: true,
              buttonText: "Configure",
              buttonActionMethodName: "AnimatorBackgroundWindow.ShowWindow")]
    public class AnimatorBackground
    {
        private static MethodInfo cachedTargetMethod;
        private static Type AnimatorWindowGraphGUIType;

        // Persistent settings
        public static Texture2D overlayTexture;
        public static float overlayOpacity = 0.1f;
        public static Vector2 overlayScale = Vector2.one;
        public static Vector2 overlayOffset = Vector2.zero;
        public static ScaleMode overlayScaleMode = ScaleMode.ScaleToFit;
        public static bool overlayTiling = false;
        public static Color overlayTint = Color.white;

        // Parallax settings
        public static bool enableParallax = false;
        public static float parallaxStrength = 0.025f;
        public static bool invertParallaxX = false;
        public static bool invertParallaxY = false;
        public static float parallaxSmoothing = 1f;

        // Runtime parallax data
        public static Vector2 currentParallaxOffset = Vector2.zero;
        public static Vector2 targetParallaxOffset = Vector2.zero;
        public static bool hasInitializedMouse = false;
        private static float lastUpdateTime = 0f;

        // Editor preferences keys
        private const string PREF_TEXTURE_PATH = "SDKatHome_AnimatorBackground_TexturePath";
        private const string PREF_TEXTURE_FULL_PATH = "SDKatHome_AnimatorBackground_TextureFullPath";
        private const string PREF_OPACITY = "SDKatHome_AnimatorBackground_Opacity";
        private const string PREF_SCALE_X = "SDKatHome_AnimatorBackground_ScaleX";
        private const string PREF_SCALE_Y = "SDKatHome_AnimatorBackground_ScaleY";
        private const string PREF_OFFSET_X = "SDKatHome_AnimatorBackground_OffsetX";
        private const string PREF_OFFSET_Y = "SDKatHome_AnimatorBackground_OffsetY";
        private const string PREF_SCALE_MODE = "SDKatHome_AnimatorBackground_ScaleMode";
        private const string PREF_TILING = "SDKatHome_AnimatorBackground_Tiling";
        private const string PREF_TINT_R = "SDKatHome_AnimatorBackground_TintR";
        private const string PREF_TINT_G = "SDKatHome_AnimatorBackground_TintG";
        private const string PREF_TINT_B = "SDKatHome_AnimatorBackground_TintB";
        private const string PREF_TINT_A = "SDKatHome_AnimatorBackground_TintA";

        // Parallax preferences
        private const string PREF_PARALLAX_ENABLE = "SDKatHome_AnimatorBackground_ParallaxEnable";
        private const string PREF_PARALLAX_STRENGTH = "SDKatHome_AnimatorBackground_ParallaxStrength";
        private const string PREF_PARALLAX_INVERT_X = "SDKatHome_AnimatorBackground_ParallaxInvertX";
        private const string PREF_PARALLAX_INVERT_Y = "SDKatHome_AnimatorBackground_ParallaxInvertY";
        private const string PREF_PARALLAX_SMOOTHING = "SDKatHome_AnimatorBackground_ParallaxSmoothing";

        public static bool prefsLoaded = false;

        public static string[] GetPreferenceKeys()
        {
            return new string[]
            {
                PREF_TEXTURE_PATH,
                PREF_TEXTURE_FULL_PATH,
                PREF_OPACITY,
                PREF_SCALE_X,
                PREF_SCALE_Y,
                PREF_OFFSET_X,
                PREF_OFFSET_Y,
                PREF_SCALE_MODE,
                PREF_TILING,
                PREF_TINT_R,
                PREF_TINT_G,
                PREF_TINT_B,
                PREF_TINT_A,
                PREF_PARALLAX_ENABLE,
                PREF_PARALLAX_STRENGTH,
                PREF_PARALLAX_INVERT_X,
                PREF_PARALLAX_INVERT_Y,
                PREF_PARALLAX_SMOOTHING
            };
        }

        public static MethodBase TargetMethod()
        {
            if (cachedTargetMethod != null)
                return cachedTargetMethod;

            try
            {
                // Find the AnimatorWindow's GraphGUI type
                var animatorWindowType = typeof(EditorWindow).Assembly.GetType("UnityEditor.Graphs.AnimatorControllerTool")
                                        ?? typeof(EditorWindow).Assembly.GetType("UnityEditor.AnimatorControllerTool");

                if (animatorWindowType == null)
                {
                    // Try alternative approach - search for types containing "AnimatorWindow"
                    var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                    foreach (var assembly in assemblies)
                    {
                        try
                        {
                            var types = assembly.GetTypes();
                            foreach (var type in types)
                            {
                                if (type.Name.Contains("AnimatorWindow") || type.Name.Contains("AnimatorControllerTool"))
                                {
                                    animatorWindowType = type;
                                    break;
                                }
                            }
                            if (animatorWindowType != null) break;
                        }
                        catch (Exception) { continue; }
                    }
                }

                // Look for GraphGUI nested type or similar
                if (animatorWindowType != null)
                {
                    var nestedTypes = animatorWindowType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
                    foreach (var nestedType in nestedTypes)
                    {
                        if (nestedType.Name.Contains("GraphGUI") || nestedType.Name.Contains("Graph"))
                        {
                            AnimatorWindowGraphGUIType = nestedType;
                            break;
                        }
                    }
                }

                // Fallback: search all types for GraphGUI-like classes
                if (AnimatorWindowGraphGUIType == null)
                {
                    var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                    foreach (var assembly in assemblies)
                    {
                        try
                        {
                            var types = assembly.GetTypes();
                            foreach (var type in types)
                            {
                                if ((type.Name.Contains("GraphGUI") || type.Name.Contains("AnimatorGraph")) &&
                                    type.GetMethod("DrawGrid", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) != null)
                                {
                                    AnimatorWindowGraphGUIType = type;
                                    break;
                                }
                            }
                            if (AnimatorWindowGraphGUIType != null) break;
                        }
                        catch (Exception) { continue; }
                    }
                }

                if (AnimatorWindowGraphGUIType != null)
                {
                    cachedTargetMethod = AnimatorWindowGraphGUIType.GetMethod("DrawGrid",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }

                return cachedTargetMethod;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static void Postfix(object __instance, Rect gridRect, float zoomLevel)
        {
            if (Event.current.type != EventType.Repaint)
                return;

            // Load preferences on first use
            if (!prefsLoaded)
            {
                LoadPreferences();
                prefsLoaded = true;
            }

            if (overlayTexture == null)
                return;

            // Update parallax if enabled
            if (enableParallax)
            {
                UpdateParallaxOffset(gridRect);

                // Force continuous repainting when parallax is active
                // This ensures the background updates as the mouse moves
                if (EditorWindow.focusedWindow != null)
                {
                    EditorWindow.focusedWindow.Repaint();
                }
            }

            DrawImageOverlay(gridRect, zoomLevel);
        }

        private static void UpdateParallaxOffset(Rect gridRect)
        {
            try
            {
                Vector2 mousePosition = Event.current.mousePosition;
                float currentTime = (float)EditorApplication.timeSinceStartup;

                // Initialize on first frame
                if (!hasInitializedMouse)
                {
                    currentParallaxOffset = Vector2.zero;
                    targetParallaxOffset = Vector2.zero;
                    lastUpdateTime = currentTime;
                    hasInitializedMouse = true;
                    return;
                }

                // Calculate delta time using EditorApplication.timeSinceStartup
                float deltaTime = currentTime - lastUpdateTime;
                lastUpdateTime = currentTime;

                // Calculate normalized mouse position relative to grid center
                Vector2 gridCenter = gridRect.center;
                Vector2 normalizedMouse = new Vector2(
                    (mousePosition.x - gridCenter.x) / (gridRect.width * 0.5f),
                    (mousePosition.y - gridCenter.y) / (gridRect.height * 0.5f)
                );

                // Clamp to reasonable bounds
                normalizedMouse.x = Mathf.Clamp(normalizedMouse.x, -1f, 1f);
                normalizedMouse.y = Mathf.Clamp(normalizedMouse.y, -1f, 1f);

                // Calculate target parallax offset
                float offsetX = normalizedMouse.x * parallaxStrength * gridRect.width;
                float offsetY = normalizedMouse.y * parallaxStrength * gridRect.height;

                // Apply inversion if enabled
                if (invertParallaxX) offsetX = -offsetX;
                if (invertParallaxY) offsetY = -offsetY;

                targetParallaxOffset = new Vector2(offsetX, offsetY);

                // Smooth interpolation using proper delta time
                if (parallaxSmoothing > 0 && deltaTime > 0)
                {
                    // Convert smoothing value to proper lerp factor
                    // Higher smoothing = slower response = lower lerp factor
                    float lerpFactor = 1f - Mathf.Exp(-deltaTime * (10f / Mathf.Max(0.1f, parallaxSmoothing)));
                    currentParallaxOffset = Vector2.Lerp(currentParallaxOffset, targetParallaxOffset, lerpFactor);
                }
                else
                {
                    currentParallaxOffset = targetParallaxOffset;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Animator Background: Error updating parallax - {ex.Message}");
            }
        }

        private static void DrawImageOverlay(Rect gridRect, float zoomLevel)
        {
            try
            {
                // Save current GUI state
                var oldColor = GUI.color;
                var oldMatrix = GUI.matrix;

                // Apply tint and opacity
                Color finalTint = overlayTint;
                finalTint.a *= overlayOpacity;
                GUI.color = finalTint;

                // Calculate overlay rectangle with parallax
                Rect overlayRect = CalculateOverlayRect(gridRect);

                if (overlayTiling)
                {
                    DrawTiledOverlay(overlayRect, gridRect);
                }
                else
                {
                    GUI.DrawTexture(overlayRect, overlayTexture, overlayScaleMode);
                }

                // Restore GUI state
                GUI.color = oldColor;
                GUI.matrix = oldMatrix;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Animator Background: Error drawing overlay - {ex.Message}");
            }
        }

        private static Rect CalculateOverlayRect(Rect gridRect)
        {
            if (overlayTexture == null)
                return gridRect;

            Vector2 scaledSize = new Vector2(gridRect.width, gridRect.height);

            // Apply custom scaling
            scaledSize.x *= overlayScale.x;
            scaledSize.y *= overlayScale.y;

            // Calculate base position with offset
            Vector2 baseOffset = overlayOffset;

            // Add parallax offset if enabled
            if (enableParallax)
            {
                baseOffset += currentParallaxOffset;
            }

            Vector2 position = new Vector2(
                gridRect.center.x - scaledSize.x * 0.5f + baseOffset.x,
                gridRect.center.y - scaledSize.y * 0.5f + baseOffset.y
            );

            return new Rect(position.x, position.y, scaledSize.x, scaledSize.y);
        }

        private static void DrawTiledOverlay(Rect overlayRect, Rect gridRect)
        {
            if (overlayTexture == null)
                return;

            float tileWidth = overlayTexture.width * overlayScale.x;
            float tileHeight = overlayTexture.height * overlayScale.y;

            // Calculate base offset with parallax
            Vector2 baseOffset = overlayOffset;
            if (enableParallax)
            {
                baseOffset += currentParallaxOffset;
            }

            // Calculate how many tiles we need
            int tilesX = Mathf.CeilToInt(gridRect.width / tileWidth) + 2;
            int tilesY = Mathf.CeilToInt(gridRect.height / tileHeight) + 2;

            // Calculate starting position with parallax
            float startX = gridRect.x + (baseOffset.x % tileWidth) - tileWidth;
            float startY = gridRect.y + (baseOffset.y % tileHeight) - tileHeight;

            // Draw tiles
            for (int x = 0; x < tilesX; x++)
            {
                for (int y = 0; y < tilesY; y++)
                {
                    Rect tileRect = new Rect(
                        startX + x * tileWidth,
                        startY + y * tileHeight,
                        tileWidth,
                        tileHeight
                    );

                    // Only draw if tile intersects with grid rect
                    if (tileRect.Overlaps(gridRect))
                    {
                        GUI.DrawTexture(tileRect, overlayTexture, ScaleMode.StretchToFill);
                    }
                }
            }
        }

        #region Preferences Management

        public static void LoadPreferences()
        {
            try
            {
                overlayOpacity = EditorPrefs.GetFloat(PREF_OPACITY, 0.1f);
                overlayScale = new Vector2(
                    EditorPrefs.GetFloat(PREF_SCALE_X, 1f),
                    EditorPrefs.GetFloat(PREF_SCALE_Y, 1f)
                );
                overlayOffset = new Vector2(
                    EditorPrefs.GetFloat(PREF_OFFSET_X, 0f),
                    EditorPrefs.GetFloat(PREF_OFFSET_Y, 0f)
                );
                overlayScaleMode = (ScaleMode)EditorPrefs.GetInt(PREF_SCALE_MODE, (int)ScaleMode.ScaleAndCrop);
                overlayTiling = EditorPrefs.GetBool(PREF_TILING, false);

                overlayTint = new Color(
                    EditorPrefs.GetFloat(PREF_TINT_R, 1f),
                    EditorPrefs.GetFloat(PREF_TINT_G, 1f),
                    EditorPrefs.GetFloat(PREF_TINT_B, 1f),
                    EditorPrefs.GetFloat(PREF_TINT_A, 1f)
                );

                // Load parallax settings
                enableParallax = EditorPrefs.GetBool(PREF_PARALLAX_ENABLE, false);
                parallaxStrength = EditorPrefs.GetFloat(PREF_PARALLAX_STRENGTH, 0.025f);
                invertParallaxX = EditorPrefs.GetBool(PREF_PARALLAX_INVERT_X, false);
                invertParallaxY = EditorPrefs.GetBool(PREF_PARALLAX_INVERT_Y, false);
                parallaxSmoothing = EditorPrefs.GetFloat(PREF_PARALLAX_SMOOTHING, 1f);

                // Load texture - try full path first, then project relative path
                string texturePath = EditorPrefs.GetString(PREF_TEXTURE_PATH, "");
                string fullTexturePath = EditorPrefs.GetString(PREF_TEXTURE_FULL_PATH, "");

                if (!string.IsNullOrEmpty(fullTexturePath) && System.IO.File.Exists(fullTexturePath))
                {
                    // Load from full system path
                    byte[] fileData = System.IO.File.ReadAllBytes(fullTexturePath);
                    Texture2D loadedTexture = new Texture2D(2, 2);
                    if (loadedTexture.LoadImage(fileData))
                    {
                        overlayTexture = loadedTexture;
                    }
                }
                else if (!string.IsNullOrEmpty(texturePath))
                {
                    // Fallback to project relative path
                    overlayTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Animator Background: Error loading preferences - {ex.Message}");
            }
        }

        public static void SavePreferences()
        {
            try
            {
                EditorPrefs.SetFloat(PREF_OPACITY, overlayOpacity);
                EditorPrefs.SetFloat(PREF_SCALE_X, overlayScale.x);
                EditorPrefs.SetFloat(PREF_SCALE_Y, overlayScale.y);
                EditorPrefs.SetFloat(PREF_OFFSET_X, overlayOffset.x);
                EditorPrefs.SetFloat(PREF_OFFSET_Y, overlayOffset.y);
                EditorPrefs.SetInt(PREF_SCALE_MODE, (int)overlayScaleMode);
                EditorPrefs.SetBool(PREF_TILING, overlayTiling);

                EditorPrefs.SetFloat(PREF_TINT_R, overlayTint.r);
                EditorPrefs.SetFloat(PREF_TINT_G, overlayTint.g);
                EditorPrefs.SetFloat(PREF_TINT_B, overlayTint.b);
                EditorPrefs.SetFloat(PREF_TINT_A, overlayTint.a);

                // Save parallax settings
                EditorPrefs.SetBool(PREF_PARALLAX_ENABLE, enableParallax);
                EditorPrefs.SetFloat(PREF_PARALLAX_STRENGTH, parallaxStrength);
                EditorPrefs.SetBool(PREF_PARALLAX_INVERT_X, invertParallaxX);
                EditorPrefs.SetBool(PREF_PARALLAX_INVERT_Y, invertParallaxY);
                EditorPrefs.SetFloat(PREF_PARALLAX_SMOOTHING, parallaxSmoothing);

                // Save texture paths - both project relative and full system path
                string texturePath = overlayTexture != null ? AssetDatabase.GetAssetPath(overlayTexture) : "";
                string fullTexturePath = "";

                if (overlayTexture != null)
                {
                    // Get full system path
                    string projectPath = System.IO.Path.GetDirectoryName(Application.dataPath);
                    if (!string.IsNullOrEmpty(texturePath))
                    {
                        fullTexturePath = System.IO.Path.Combine(projectPath, texturePath);
                    }
                }

                EditorPrefs.SetString(PREF_TEXTURE_PATH, texturePath);
                EditorPrefs.SetString(PREF_TEXTURE_FULL_PATH, fullTexturePath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Animator Background: Error saving preferences - {ex.Message}");
            }
        }

        #endregion

        // Cleanup on script reload
        [UnityEditor.Callbacks.DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            prefsLoaded = false;
            cachedTargetMethod = null;
            AnimatorWindowGraphGUIType = null;

            // Reset parallax state
            currentParallaxOffset = Vector2.zero;
            targetParallaxOffset = Vector2.zero;
            hasInitializedMouse = false;
            lastUpdateTime = 0f;
        }
    }
}
#endif