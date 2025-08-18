#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;
using System;

namespace SDKatHome
{
    public class PlayAudioMultiEditWindow : EditorWindow
    {
        private Vector2 scrollPos;
        private List<VRCAnimatorPlayAudio> foundComponents = new List<VRCAnimatorPlayAudio>();
        private List<VRCAnimatorPlayAudio> selectedComponents = new List<VRCAnimatorPlayAudio>();
        private SerializedObject serializedObject;
        private AnimatorController targetController;
        private string lastSelectionHash = "";
        private AudioSource tempAudioSource; // For drag and drop

        public static void ShowWindow()
        {
            var window = GetWindow<PlayAudioMultiEditWindow>("PlayAudio Multi-Editor");
            window.minSize = new Vector2(500, 400);
        }

        void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
            Undo.undoRedoPerformed += OnUndoRedo;
            RefreshFromAnimatorWindow();
        }

        void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            Undo.undoRedoPerformed -= OnUndoRedo;
        }

        void OnUndoRedo()
        {
            // Refresh the serialized object when undo/redo occurs
            if (serializedObject != null)
            {
                serializedObject.Update();
                Repaint();
            }
        }

        void OnEditorUpdate()
        {
            // Check for changes periodically
            RefreshFromAnimatorWindow();
        }

        void OnFocus()
        {
            RefreshFromAnimatorWindow();
        }

        void RefreshFromAnimatorWindow()
        {
            var currentController = GetActiveAnimatorController();
            var selectedStates = TryGetSelectedStates();

            string newHash = currentController?.GetInstanceID().ToString() + "_" +
                           string.Join(",", selectedStates.Select(s => s.GetInstanceID().ToString()));

            if (newHash != lastSelectionHash)
            {
                targetController = currentController;
                RefreshSelection(selectedStates);
                lastSelectionHash = newHash;
            }
        }

        List<AnimatorState> TryGetSelectedStates()
        {
            var states = new List<AnimatorState>();

            // Method 1: Try Selection API first
            foreach (var obj in Selection.objects)
            {
                if (obj is AnimatorState state)
                {
                    states.Add(state);
                }
            }

            if (states.Count > 0) return states;

            // Method 2: Try to get from Animator window using reflection
            try
            {
                var animatorWindows = Resources.FindObjectsOfTypeAll<EditorWindow>();
                EditorWindow animatorWindow = null;

                var possibleTypes = new[] {
                    "UnityEditor.Graphs.AnimatorControllerTool",
                    "UnityEditor.AnimatorControllerTool",
                    "UnityEditor.AnimatorWindow",
                    "UnityEditor.Graphs.AnimatorWindow"
                };

                foreach (var typeName in possibleTypes)
                {
                    animatorWindow = animatorWindows.FirstOrDefault(w => w.GetType().FullName == typeName);
                    if (animatorWindow != null) break;
                }

                if (animatorWindow != null)
                {
                    var windowType = animatorWindow.GetType();

                    var possiblePaths = new[]
                    {
                        new[] { "stateMachineGraph", "selection" },
                        new[] { "m_StateMachineGraph", "selection" },
                        new[] { "stateMachineGraphGUI", "selection" },
                        new[] { "m_StateMachineGraphGUI", "selection" },
                        new[] { "selection" },
                        new[] { "m_Selection" }
                    };

                    foreach (var path in possiblePaths)
                    {
                        object current = animatorWindow;
                        bool pathValid = true;

                        foreach (var step in path)
                        {
                            var field = current?.GetType().GetField(step, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                            var property = current?.GetType().GetProperty(step, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

                            if (field != null)
                            {
                                current = field.GetValue(current);
                            }
                            else if (property != null)
                            {
                                current = property.GetValue(current);
                            }
                            else
                            {
                                pathValid = false;
                                break;
                            }
                        }

                        if (pathValid && current != null)
                        {
                            states.AddRange(ExtractStatesFromObject(current));
                            if (states.Count > 0) break;
                        }
                    }
                }
            }
            catch (Exception) { }

            return states;
        }

        List<AnimatorState> ExtractStatesFromObject(object obj)
        {
            var states = new List<AnimatorState>();

            if (obj == null) return states;

            if (obj is AnimatorState state)
            {
                states.Add(state);
                return states;
            }

            var objType = obj.GetType();
            var stateFields = new[] { "states", "selectedStates", "m_States", "m_SelectedStates" };

            foreach (var fieldName in stateFields)
            {
                var field = objType.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var property = objType.GetProperty(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                object statesObj = null;
                if (field != null) statesObj = field.GetValue(obj);
                else if (property != null) statesObj = property.GetValue(obj);

                if (statesObj != null)
                {
                    if (statesObj is IEnumerable<AnimatorState> stateEnum)
                    {
                        states.AddRange(stateEnum);
                    }
                    else if (statesObj is System.Collections.IEnumerable enumerable)
                    {
                        foreach (var item in enumerable)
                        {
                            if (item is AnimatorState animState)
                            {
                                states.Add(animState);
                            }
                        }
                    }
                }
            }

            return states;
        }

        void RefreshSelection(List<AnimatorState> selectedStates = null)
        {
            foundComponents.Clear();
            selectedComponents.Clear();

            if (selectedStates == null || selectedStates.Count == 0)
            {
                serializedObject = null;
                Repaint();
                return;
            }

            foreach (var state in selectedStates)
            {
                var playAudioBehaviors = state.behaviours.OfType<VRCAnimatorPlayAudio>();
                foundComponents.AddRange(playAudioBehaviors);
            }

            selectedComponents.AddRange(foundComponents);

            if (selectedComponents.Count > 0)
            {
                serializedObject = new SerializedObject(selectedComponents.ToArray());
            }
            else
            {
                serializedObject = null;
            }

            Repaint();
        }

        AnimatorController GetActiveAnimatorController()
        {
            var animatorWindows = Resources.FindObjectsOfTypeAll<EditorWindow>();

            var possibleTypes = new[] {
                "UnityEditor.Graphs.AnimatorControllerTool",
                "UnityEditor.AnimatorControllerTool",
                "UnityEditor.AnimatorWindow",
                "UnityEditor.Graphs.AnimatorWindow"
            };

            foreach (var typeName in possibleTypes)
            {
                var animatorWindow = animatorWindows.FirstOrDefault(w => w.GetType().FullName == typeName);
                if (animatorWindow != null)
                {
                    var windowType = animatorWindow.GetType();
                    var controllerFields = new[] { "animatorController", "m_AnimatorController", "controller" };

                    foreach (var fieldName in controllerFields)
                    {
                        var property = windowType.GetProperty(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (property != null)
                        {
                            var controller = property.GetValue(animatorWindow) as AnimatorController;
                            if (controller != null) return controller;
                        }

                        var field = windowType.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (field != null)
                        {
                            var controller = field.GetValue(animatorWindow) as AnimatorController;
                            if (controller != null) return controller;
                        }
                    }
                }
            }

            return null;
        }

        string GetRelativePath(Transform target, Transform root)
        {
            if (target == root) return "";

            string path = target.name;
            Transform current = target.parent;

            while (current != null && current != root)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }

            return current == root ? path : "";
        }

        void OnGUI()
        {
            EditorGUILayout.BeginVertical();

            // Header
            EditorGUILayout.LabelField("VRC Animator PlayAudio Multi-Editor", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            if (targetController != null)
            {
                EditorGUILayout.LabelField("Controller:", EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"  {targetController.name}", EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "No Animator Controller detected. Open an Animator Controller in the Animator window.",
                    MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUILayout.Space();

            var selectedStates = TryGetSelectedStates();
            EditorGUILayout.LabelField($"Selected States: {selectedStates.Count}");
            EditorGUILayout.LabelField($"PlayAudio Components: {foundComponents.Count}");

            if (selectedStates.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No animator states selected. Select states in the Animator window to edit their PlayAudio components here.\n\n" +
                    "Tip: Hold Ctrl/Cmd to select multiple states.",
                    MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            if (foundComponents.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    $"Selected {selectedStates.Count} state(s), but none contain VRC Animator PlayAudio behaviors.\n\n" +
                    "Add PlayAudio behaviors to states to edit them here.",
                    MessageType.Warning);
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUILayout.Space();

            // Audio Source drag & drop section
            EditorGUILayout.LabelField("Quick Audio Source Setup:", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Drag an AudioSource from the hierarchy to automatically set the Source Path for all selected components.", EditorStyles.helpBox);

            EditorGUI.BeginChangeCheck();
            tempAudioSource = EditorGUILayout.ObjectField("AudioSource", tempAudioSource, typeof(AudioSource), true) as AudioSource;
            if (EditorGUI.EndChangeCheck() && tempAudioSource != null)
            {
                // Find the avatar root (component with VRCAvatarDescriptor)
                var avatarDescriptor = tempAudioSource.GetComponentInParent<VRCAvatarDescriptor>();
                if (avatarDescriptor != null)
                {
                    string relativePath = GetRelativePath(tempAudioSource.transform, avatarDescriptor.transform);

                    // Apply to all selected components
                    if (serializedObject != null)
                    {
                        Undo.RecordObjects(selectedComponents.ToArray(), "Set AudioSource Path");

                        var sourcePath = serializedObject.FindProperty("SourcePath");
                        sourcePath.stringValue = relativePath;
                        serializedObject.ApplyModifiedProperties();

                        EditorUtility.SetDirty(targetController);
                    }
                }
                else
                {
                    Debug.LogError("The AudioSource must be part of an avatar hierarchy with a VRCAvatarDescriptor component.");
                }

                // Clear the temp field
                tempAudioSource = null;
            }

            EditorGUILayout.Space();

            // Show selected states info
            EditorGUILayout.LabelField("Selected States with PlayAudio:", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            foreach (var state in selectedStates)
            {
                var playAudioCount = state.behaviours.OfType<VRCAnimatorPlayAudio>().Count();
                if (playAudioCount > 0)
                {
                    EditorGUILayout.LabelField($"• {state.name} ({playAudioCount} component{(playAudioCount > 1 ? "s" : "")})", EditorStyles.miniLabel);
                }
            }
            EditorGUI.indentLevel--;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField($"Editing {foundComponents.Count} PlayAudio component(s):", EditorStyles.boldLabel);

            // Scroll area for the multi-edit interface
            scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

            if (serializedObject != null)
            {
                serializedObject.Update();
                DrawMultiEditInspector();

                // Check for changes and apply with undo support
                if (serializedObject.hasModifiedProperties)
                {
                    Undo.RecordObjects(selectedComponents.ToArray(), "Modify PlayAudio Components");
                    serializedObject.ApplyModifiedProperties();
                    EditorUtility.SetDirty(targetController);
                }
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        void DrawMultiEditInspector()
        {
            // Get all the serialized properties
            var sourcePath = serializedObject.FindProperty("SourcePath");
            var playbackOrder = serializedObject.FindProperty("PlaybackOrder");
            var parameterName = serializedObject.FindProperty("ParameterName");
            var pitch = serializedObject.FindProperty("Pitch");
            var pitchApplySettings = serializedObject.FindProperty("PitchApplySettings");
            var volume = serializedObject.FindProperty("Volume");
            var volumeApplySettings = serializedObject.FindProperty("VolumeApplySettings");
            var clips = serializedObject.FindProperty("Clips");
            var clipsApplySettings = serializedObject.FindProperty("ClipsApplySettings");
            var delayInSeconds = serializedObject.FindProperty("DelayInSeconds");
            var loop = serializedObject.FindProperty("Loop");
            var loopApplySettings = serializedObject.FindProperty("LoopApplySettings");
            var stopOnEnter = serializedObject.FindProperty("StopOnEnter");
            var playOnEnter = serializedObject.FindProperty("PlayOnEnter");
            var stopOnExit = serializedObject.FindProperty("StopOnExit");
            var playOnExit = serializedObject.FindProperty("PlayOnExit");

            // Source Path
            EditorGUILayout.PropertyField(sourcePath, new GUIContent("Source Path"));

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("AudioClips", EditorStyles.boldLabel);

            // Playback Order and Clips Apply Settings
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(clipsApplySettings.hasMultipleDifferentValues ||
                    clipsApplySettings.intValue == (int)VRC_AnimatorPlayAudio.ApplySettings.NeverApply))
                {
                    EditorGUILayout.PropertyField(playbackOrder);
                }
                EditorGUILayout.PropertyField(clipsApplySettings, GUIContent.none, GUILayout.Width(150));
            }

            // Parameter selection for parameter-based playback
            if (!playbackOrder.hasMultipleDifferentValues &&
                playbackOrder.intValue == (int)VRCAnimatorPlayAudio.Order.Parameter)
            {
                EditorGUILayout.HelpBox("Parameter-based clip selection", MessageType.Info);
                DrawParameterField(parameterName, "Parameter Name");
            }

            // Clips array
            using (new EditorGUI.DisabledScope(clipsApplySettings.hasMultipleDifferentValues ||
                clipsApplySettings.intValue == (int)VRC_AnimatorPlayAudio.ApplySettings.NeverApply))
            {
                EditorGUILayout.PropertyField(clips, true);
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("AudioSource Settings", EditorStyles.boldLabel);

            // Volume
            DrawRangeProperty(volume, volumeApplySettings, "Volume", 0f, 1f);

            // Pitch
            DrawRangeProperty(pitch, pitchApplySettings, "Pitch", -3f, 3f);

            // Loop
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(loopApplySettings.hasMultipleDifferentValues ||
                    loopApplySettings.intValue == (int)VRC_AnimatorPlayAudio.ApplySettings.NeverApply))
                {
                    EditorGUILayout.PropertyField(loop);
                }
                EditorGUILayout.PropertyField(loopApplySettings, GUIContent.none, GUILayout.Width(150));
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Play Settings", EditorStyles.boldLabel);

            // Play/Stop settings
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("On Enter:", GUILayout.Width(80));
                EditorGUILayout.PropertyField(stopOnEnter, new GUIContent("Stop"), GUILayout.Width(80));
                EditorGUILayout.PropertyField(playOnEnter, new GUIContent("Play"), GUILayout.Width(80));
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("On Exit:", GUILayout.Width(80));
                EditorGUILayout.PropertyField(stopOnExit, new GUIContent("Stop"), GUILayout.Width(80));
                EditorGUILayout.PropertyField(playOnExit, new GUIContent("Play"), GUILayout.Width(80));
            }

            // Delay
            using (new EditorGUI.DisabledScope(playOnEnter.hasMultipleDifferentValues || !playOnEnter.boolValue))
            {
                EditorGUI.BeginChangeCheck();
                EditorGUILayout.PropertyField(delayInSeconds, new GUIContent("Play Delay (seconds)"));
                if (EditorGUI.EndChangeCheck())
                {
                    delayInSeconds.floatValue = Mathf.Clamp(delayInSeconds.floatValue, 0, 60);
                }
            }

            // Warning message
            bool showWarning = (!clipsApplySettings.hasMultipleDifferentValues && clipsApplySettings.intValue == (int)VRC_AnimatorPlayAudio.ApplySettings.ApplyIfStopped) ||
                             (!volumeApplySettings.hasMultipleDifferentValues && volumeApplySettings.intValue == (int)VRC_AnimatorPlayAudio.ApplySettings.ApplyIfStopped) ||
                             (!pitchApplySettings.hasMultipleDifferentValues && pitchApplySettings.intValue == (int)VRC_AnimatorPlayAudio.ApplySettings.ApplyIfStopped) ||
                             (!loopApplySettings.hasMultipleDifferentValues && loopApplySettings.intValue == (int)VRC_AnimatorPlayAudio.ApplySettings.ApplyIfStopped);

            if (showWarning && (!stopOnEnter.hasMultipleDifferentValues && !stopOnEnter.boolValue))
            {
                EditorGUILayout.HelpBox("Settings with 'Apply If Stopped' will only apply if the audio source isn't playing when entering the state.", MessageType.Info);
            }
        }

        void DrawRangeProperty(SerializedProperty rangeProperty, SerializedProperty applySettingsProperty, string label, float min, float max)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(applySettingsProperty.hasMultipleDifferentValues ||
                    applySettingsProperty.intValue == (int)VRC_AnimatorPlayAudio.ApplySettings.NeverApply))
                {
                    EditorGUILayout.LabelField($"Random {label}", GUILayout.Width(100));

                    var minProp = rangeProperty.FindPropertyRelative("x");
                    var maxProp = rangeProperty.FindPropertyRelative("y");

                    EditorGUILayout.LabelField("Min", GUILayout.Width(30));
                    EditorGUI.BeginChangeCheck();
                    float newMin = EditorGUILayout.FloatField(minProp.floatValue, GUILayout.Width(60));
                    if (EditorGUI.EndChangeCheck())
                    {
                        minProp.floatValue = Mathf.Clamp(newMin, min, max);
                        serializedObject.ApplyModifiedProperties(); // Force immediate sync
                        serializedObject.Update(); // Refresh from targets
                    }

                    EditorGUILayout.LabelField("Max", GUILayout.Width(30));
                    EditorGUI.BeginChangeCheck();
                    float newMax = EditorGUILayout.FloatField(maxProp.floatValue, GUILayout.Width(60));
                    if (EditorGUI.EndChangeCheck())
                    {
                        maxProp.floatValue = Mathf.Clamp(newMax, min, max);
                        serializedObject.ApplyModifiedProperties(); // Force immediate sync
                        serializedObject.Update(); // Refresh from targets
                    }
                }
                EditorGUILayout.PropertyField(applySettingsProperty, GUIContent.none, GUILayout.Width(150));
            }
        }

        void DrawParameterField(SerializedProperty parameterName, string label)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(label);

                if (parameterName.hasMultipleDifferentValues)
                {
                    EditorGUILayout.LabelField("—", EditorStyles.popup, GUILayout.Width(100));
                }
                else
                {
                    EditorGUILayout.LabelField("Parameter:", GUILayout.Width(70));
                }

                EditorGUILayout.PropertyField(parameterName, GUIContent.none);
            }
        }
    }
}
#endif