#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace SDKatHome.Patches
{
    [SDKPatch("Animator Transition Colors",
      "Colors animator transitions based on given conditions",
      "Animator Tools",
      usePrefix: true,
      usePostfix: false,
      buttonText: "Configure",
      buttonActionMethodName: "AnimatorTransitionColorWindow.ShowWindow")]
    [InitializeOnLoad]
    public static class AnimatorTransitionColorPatch
    {
        // Dictionary to store user-defined color rules
        private static List<TransitionColorRule> _colorRules = new List<TransitionColorRule>();

        private static MethodInfo _drawEdgeMethod;
        private static Dictionary<int, AnimatorStateTransition> _idToTransitionMap = new Dictionary<int, AnimatorStateTransition>();

        // Animation settings
        private static bool _enableFlashAnimation = true;
        private static float _flashSpeed = 1.0f;
        private static Color _incomingFlashColor = Color.blue;  // Blue for incoming transitions
        private static Color _outgoingFlashColor = Color.cyan;    // Cyan for outgoing transitions
        private static float _flashIntensity = 0.8f;
        private static float _sequenceDelay = 0.0f;              // Delay between incoming and outgoing flashes
        private static bool _enableSequentialFlash = true;       // Enable/disable sequential mode

        // Animation state
        private static double _animationStartTime;
        private static List<AnimatorState> _lastSelectedStates = new List<AnimatorState>();
        private static HashSet<AnimatorStateTransition> _incomingTransitions = new HashSet<AnimatorStateTransition>();
        private static HashSet<AnimatorStateTransition> _outgoingTransitions = new HashSet<AnimatorStateTransition>();

        // Animation phases
        private enum FlashPhase
        {
            Incoming,
            Delay,
            Outgoing,
            Finished
        }

        // Statistics
        private static int _frameCount = 0;
        private static int _lastStatsFrame = 0;

        public static string[] GetPreferenceKeys()
        {
            return new string[]
            {
                "SDKatHome.AnimatorTransition.EnableFlash",
                "SDKatHome.AnimatorTransition.FlashSpeed",
                "SDKatHome.AnimatorTransition.IncomingFlashColor",
                "SDKatHome.AnimatorTransition.OutgoingFlashColor",
                "SDKatHome.AnimatorTransition.FlashIntensity",
                "SDKatHome.AnimatorTransition.SequenceDelay",
                "SDKatHome.AnimatorTransition.EnableSequential",
                "SDKatHome.AnimatorTransitionColorRules"
            };
        }

        // Static constructor for InitializeOnLoad
        static AnimatorTransitionColorPatch()
        {
            // Register for editor update
            EditorApplication.update += OnEditorUpdate;

            // Load saved rules from EditorPrefs
            LoadColorRules();
            LoadAnimationSettings();

            // Initialize animation timer
            _animationStartTime = EditorApplication.timeSinceStartup;
        }

        // Editor update method
        private static void OnEditorUpdate()
        {
            _frameCount++;

            // Update transition cache periodically
            if (_frameCount % 300 == 0 || _frameCount - _lastStatsFrame >= 300)
            {
                RefreshTransitions();
                _lastStatsFrame = _frameCount;
            }

            // Check for state selection changes
            if (_enableFlashAnimation)
            {
                CheckForStateSelectionChange();
            }

            // Repaint animator windows to update the animation
            if (_enableFlashAnimation && (_incomingTransitions.Count > 0 || _outgoingTransitions.Count > 0))
            {
                RepaintAnimatorWindows();
            }
        }

        /// <summary>
        /// Check if the selected states have changed and update related transitions
        /// </summary>
        private static void CheckForStateSelectionChange()
        {
            List<AnimatorState> currentSelectedStates = GetSelectedAnimatorStates();

            // Compare current selection with previous selection
            bool selectionChanged = false;

            if (_lastSelectedStates == null)
            {
                _lastSelectedStates = new List<AnimatorState>();
            }

            // Check if selection count changed
            if (currentSelectedStates.Count != _lastSelectedStates.Count)
            {
                selectionChanged = true;
            }
            else
            {
                // Check if any states are different
                for (int i = 0; i < currentSelectedStates.Count; i++)
                {
                    if (currentSelectedStates[i] != _lastSelectedStates[i])
                    {
                        selectionChanged = true;
                        break;
                    }
                }
            }

            if (selectionChanged)
            {
                _lastSelectedStates.Clear();
                _lastSelectedStates.AddRange(currentSelectedStates);
                _animationStartTime = EditorApplication.timeSinceStartup;

                // Update related transitions for all selected states
                UpdateRelatedTransitions(currentSelectedStates);
            }
        }

        /// <summary>
        /// Get all currently selected animator states (excluding transitions and other objects)
        /// </summary>
        private static List<AnimatorState> GetSelectedAnimatorStates()
        {
            List<AnimatorState> selectedStates = new List<AnimatorState>();

            // Check all selected objects
            if (Selection.objects != null)
            {
                foreach (var obj in Selection.objects)
                {
                    if (obj is AnimatorState state)
                    {
                        selectedStates.Add(state);
                    }
                    // Explicitly exclude transitions - we don't want to animate when only transitions are selected
                    // if (obj is AnimatorStateTransition) - do nothing
                }
            }

            // Also check single selection
            if (Selection.activeObject is AnimatorState singleState && !selectedStates.Contains(singleState))
            {
                selectedStates.Add(singleState);
            }

            return selectedStates;
        }

        /// <summary>
        /// Update the list of transitions related to the selected states
        /// </summary>
        private static void UpdateRelatedTransitions(List<AnimatorState> selectedStates)
        {
            _incomingTransitions.Clear();
            _outgoingTransitions.Clear();

            if (selectedStates == null || selectedStates.Count == 0)
                return;

            AnimatorController controller = GetCurrentAnimatorController();
            if (controller == null)
                return;

            // Find all transitions from and to the selected states
            foreach (var layer in controller.layers)
            {
                if (layer.stateMachine != null)
                {
                    FindRelatedTransitionsInStateMachine(layer.stateMachine, selectedStates);
                }
            }
        }

        /// <summary>
        /// Find related transitions in a state machine for multiple selected states
        /// </summary>
        private static void FindRelatedTransitionsInStateMachine(AnimatorStateMachine stateMachine, List<AnimatorState> selectedStates)
        {
            // Check all states in this state machine
            foreach (var childState in stateMachine.states)
            {
                if (childState.state == null)
                    continue;

                // If this is one of the selected states, add all its outgoing transitions
                if (selectedStates.Contains(childState.state))
                {
                    foreach (var transition in childState.state.transitions)
                    {
                        _outgoingTransitions.Add(transition);
                    }
                }
                else
                {
                    // For other states, check if they have transitions TO any of the selected states
                    foreach (var transition in childState.state.transitions)
                    {
                        if (selectedStates.Contains(transition.destinationState))
                        {
                            _incomingTransitions.Add(transition);
                        }
                    }
                }
            }

            // Check any state transitions that go to any of the selected states
            if (stateMachine.anyStateTransitions != null)
            {
                foreach (var transition in stateMachine.anyStateTransitions)
                {
                    if (selectedStates.Contains(transition.destinationState))
                    {
                        _incomingTransitions.Add(transition);
                    }
                }
            }

            // Recursively check child state machines
            foreach (var childStateMachine in stateMachine.stateMachines)
            {
                if (childStateMachine.stateMachine != null)
                {
                    FindRelatedTransitionsInStateMachine(childStateMachine.stateMachine, selectedStates);
                }
            }
        }

        /// <summary>
        /// Repaint all animator windows to update the animation
        /// </summary>
        private static void RepaintAnimatorWindows()
        {
            EditorWindow[] allWindows = Resources.FindObjectsOfTypeAll<EditorWindow>();
            foreach (EditorWindow window in allWindows)
            {
                if (window.titleContent.text.Contains("Animator"))
                {
                    window.Repaint();
                }
            }
        }

        private static FlashPhase GetCurrentFlashPhase()
        {
            if (!_enableSequentialFlash)
                return FlashPhase.Incoming; // Always show incoming/outgoing simultaneously

            double currentTime = EditorApplication.timeSinceStartup;
            double elapsed = currentTime - _animationStartTime;

            // Calculate duration for one complete flash cycle based on flash speed
            double oneFlashCycle = 1.0 / _flashSpeed; // One complete sine wave cycle
            double delayDuration = _sequenceDelay;
            // Total cycle: incoming + outgoing + delay (delay only at end of cycle)
            double totalCycleDuration = oneFlashCycle + oneFlashCycle + delayDuration;

            // Loop the animation by using modulo
            elapsed = elapsed % totalCycleDuration;

            if (elapsed < oneFlashCycle)
                return FlashPhase.Incoming;
            else if (elapsed < oneFlashCycle + oneFlashCycle)
                return FlashPhase.Outgoing;
            else
                return FlashPhase.Delay; // Delay only at end of complete cycle
        }

        /// <summary>
        /// Calculate the flash alpha based on current time
        /// </summary>
        private static float GetFlashAlpha(FlashPhase phase)
        {
            if (phase == FlashPhase.Delay)
                return 0f;

            double currentTime = EditorApplication.timeSinceStartup;
            double elapsed = currentTime - _animationStartTime;

            double oneFlashCycle = 1.0 / _flashSpeed;
            double delayDuration = _sequenceDelay;
            // Total cycle: incoming + outgoing + delay (delay only at end of cycle)
            double totalCycleDuration = oneFlashCycle + oneFlashCycle + delayDuration;

            // Loop the elapsed time
            elapsed = elapsed % totalCycleDuration;

            double phaseStartTime = 0;
            double phaseDuration = oneFlashCycle;

            // Determine the start time and duration for the current phase
            if (phase == FlashPhase.Incoming)
            {
                phaseStartTime = 0;
                phaseDuration = oneFlashCycle;
            }
            else if (phase == FlashPhase.Outgoing)
            {
                phaseStartTime = oneFlashCycle; // Starts immediately after incoming
                phaseDuration = oneFlashCycle;
            }

            // Check if we're in the correct phase
            if (elapsed < phaseStartTime || elapsed >= phaseStartTime + phaseDuration)
                return 0f;

            // Calculate position within this flash cycle (0 to 1)
            double phaseElapsed = elapsed - phaseStartTime;
            double phaseProgress = phaseElapsed / phaseDuration;

            // Create a smooth fade in -> peak -> fade out cycle
            // Use a sine wave that goes from 0 to π (not 0 to 2π)
            // This ensures it starts at 0, peaks at middle, and ends at 0
            float wave = Mathf.Sin((float)(phaseProgress * Math.PI));

            // Apply intensity (wave is already 0 to 1 range)
            return wave * _flashIntensity;
        }

        /// <summary>
        /// Check if a transition should flash
        /// </summary>
        private static bool ShouldFlashTransition(AnimatorStateTransition transition, out Color flashColor, out float alpha)
        {
            flashColor = Color.white;
            alpha = 0f;

            if (!_enableFlashAnimation)
                return false;

            FlashPhase currentPhase = GetCurrentFlashPhase();
            bool isIncoming = _incomingTransitions.Contains(transition);
            bool isOutgoing = _outgoingTransitions.Contains(transition);

            if (_enableSequentialFlash)
            {
                // Sequential mode: flash incoming first, then outgoing, then repeat
                switch (currentPhase)
                {
                    case FlashPhase.Incoming:
                        if (isIncoming)
                        {
                            flashColor = _incomingFlashColor;
                            alpha = GetFlashAlpha(FlashPhase.Incoming);
                            return true;
                        }
                        break;

                    case FlashPhase.Outgoing:
                        if (isOutgoing)
                        {
                            flashColor = _outgoingFlashColor;
                            alpha = GetFlashAlpha(FlashPhase.Outgoing);
                            return true;
                        }
                        break;

                    case FlashPhase.Delay:
                        return false; // No flashing during delay phases
                }
            }
            else
            {
                // Simultaneous mode: flash both at the same time with different colors
                if (isIncoming)
                {
                    flashColor = _incomingFlashColor;
                    alpha = GetFlashAlpha(FlashPhase.Incoming);
                    return true;
                }
                else if (isOutgoing)
                {
                    flashColor = _outgoingFlashColor;
                    alpha = GetFlashAlpha(FlashPhase.Incoming); // Use same timing
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Find the DrawEdge method in the EdgeGUI class
        /// </summary>
        public static MethodBase TargetMethod()
        {
            try
            {
                // Get all loaded assemblies
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

                Type edgeGUIType = null;

                // Try the direct path we know
                foreach (Assembly assembly in assemblies)
                {
                    try
                    {
                        edgeGUIType = assembly.GetType("UnityEditor.Graphs.AnimationStateMachine.EdgeGUI");
                        if (edgeGUIType != null)
                            break;
                    }
                    catch { }
                }

                if (edgeGUIType == null)
                    return null;

                // Get the DrawEdge method
                _drawEdgeMethod = edgeGUIType.GetMethod("DrawEdge",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                if (_drawEdgeMethod == null)
                    return null;

                // Initialize by refreshing transitions
                RefreshTransitions();

                return _drawEdgeMethod;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SDK@Home] Error finding method to patch: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Refresh all transitions in the current animator
        /// </summary>
        private static void RefreshTransitions()
        {
            try
            {
                // Clear previous cache
                _idToTransitionMap.Clear();

                // Get all transitions from all layers
                AnimatorController controller = GetCurrentAnimatorController();
                if (controller == null)
                    return;

                // Process each layer
                foreach (AnimatorControllerLayer layer in controller.layers)
                {
                    if (layer.stateMachine != null)
                    {
                        ProcessStateMachine(layer.stateMachine);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SDK@Home] Error refreshing transitions: {ex.Message}");
            }
        }

        /// <summary>
        /// Process all transitions in a state machine
        /// </summary>
        private static void ProcessStateMachine(AnimatorStateMachine stateMachine)
        {
            // Process all states
            foreach (ChildAnimatorState childState in stateMachine.states)
            {
                if (childState.state == null)
                    continue;

                foreach (AnimatorStateTransition transition in childState.state.transitions)
                {
                    if (transition == null)
                        continue;

                    int transitionId = transition.GetInstanceID();
                    _idToTransitionMap[transitionId] = transition;
                }
            }

            // Process any state transitions
            if (stateMachine.anyStateTransitions != null)
            {
                foreach (AnimatorStateTransition transition in stateMachine.anyStateTransitions)
                {
                    if (transition == null)
                        continue;

                    int transitionId = transition.GetInstanceID();
                    _idToTransitionMap[transitionId] = transition;
                }
            }

            // Process entry transitions
            if (stateMachine.entryTransitions != null)
            {
                foreach (AnimatorTransition transition in stateMachine.entryTransitions)
                {
                    if (transition == null)
                        continue;
                }
            }

            // Recursively process child state machines
            foreach (ChildAnimatorStateMachine childStateMachine in stateMachine.stateMachines)
            {
                if (childStateMachine.stateMachine != null)
                {
                    ProcessStateMachine(childStateMachine.stateMachine);
                }
            }
        }

        /// <summary>
        /// Get the current animator controller
        /// </summary>
        private static AnimatorController GetCurrentAnimatorController()
        {
            try
            {
                // First try selection
                if (Selection.activeObject is AnimatorController controller)
                {
                    return controller;
                }

                // Try through transition
                if (Selection.activeObject is AnimatorStateTransition transition)
                {
                    SerializedObject serializedObj = new SerializedObject(transition);
                    SerializedProperty prop = serializedObj.FindProperty("m_AnimatorController");
                    if (prop != null && prop.objectReferenceValue is AnimatorController animController)
                    {
                        return animController;
                    }
                }

                // Look for animator windows
                EditorWindow[] allWindows = Resources.FindObjectsOfTypeAll<EditorWindow>();
                foreach (EditorWindow window in allWindows)
                {
                    if (window.titleContent.text.Contains("Animator"))
                    {
                        // Try reflection to get controller
                        PropertyInfo[] props = window.GetType().GetProperties();
                        foreach (PropertyInfo prop in props)
                        {
                            if (prop.Name.Contains("controller") || prop.Name.Contains("Controller"))
                            {
                                try
                                {
                                    object value = prop.GetValue(window, null);
                                    if (value is AnimatorController animController)
                                    {
                                        return animController;
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SDK@Home] Error getting animator controller: {ex.Message}");
            }

            return null;
        }

        [HarmonyPrefix]
        public static void Prefix(object __instance, object edge, Texture2D tex, ref Color color, object info, bool viewHasLiveLinkExactEdge)
        {
            try
            {
                // Access the 'transitions' field from the info object
                if (info != null)
                {
                    // Get the 'transitions' field
                    FieldInfo transitionsField = info.GetType().GetField("transitions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    if (transitionsField != null)
                    {
                        object transitionsObj = transitionsField.GetValue(info);

                        // Check if it's a list or collection
                        if (transitionsObj is IEnumerable transitions)
                        {
                            // Iterate through the transitions
                            foreach (var item in transitions)
                            {
                                // Try to get the transition object
                                AnimatorStateTransition transition = item as AnimatorStateTransition;

                                // If it's not directly an AnimatorStateTransition, it might be a wrapper object
                                if (transition == null && item != null)
                                {
                                    // Try to get the transition from a property or field
                                    PropertyInfo transitionProp = item.GetType().GetProperty("transition",
                                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                                    if (transitionProp != null)
                                    {
                                        try
                                        {
                                            transition = transitionProp.GetValue(item, null) as AnimatorStateTransition;
                                        }
                                        catch { }
                                    }

                                    // Try fields if property didn't work
                                    if (transition == null)
                                    {
                                        FieldInfo transitionField = item.GetType().GetField("transition",
                                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                                        if (transitionField != null)
                                        {
                                            try
                                            {
                                                transition = transitionField.GetValue(item) as AnimatorStateTransition;
                                            }
                                            catch { }
                                        }
                                    }
                                }

                                // If we found a transition, check if it's selected first
                                if (transition != null)
                                {
                                    // Check for flash animation first (highest priority)
                                    Color flashColor;
                                    float flashAlpha;
                                    if (ShouldFlashTransition(transition, out flashColor, out flashAlpha))
                                    {
                                        color = Color.Lerp(color, flashColor, flashAlpha);
                                        return;
                                    }

                                    // Skip coloring if this transition is currently selected
                                    if (IsTransitionSelected(transition))
                                        return;

                                    // Check conditions against our color rules (lowest priority)
                                    if (_colorRules.Count > 0)
                                    {
                                        foreach (var rule in _colorRules)
                                        {
                                            if (MatchesRule(transition, rule))
                                            {
                                                color = rule.Color;
                                                return; // Apply the first matching rule's color
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Check if a transition matches a color rule
        /// </summary>
        private static bool MatchesRule(AnimatorStateTransition transition, TransitionColorRule rule)
        {
            try
            {
                // Check each condition in the transition
                AnimatorCondition[] conditions = transition.conditions;
                if (conditions != null)
                {
                    // For each condition in the transition
                    foreach (AnimatorCondition condition in conditions)
                    {
                        // If the parameter name matches
                        if (condition.parameter == rule.ParameterName)
                        {
                            // For bool parameters (If/IfNot)
                            if (rule.ParameterType == AnimatorControllerParameterType.Bool)
                            {
                                bool ruleExpectsTrue = rule.ThresholdValue >= 0.5f;

                                // For "If" mode in the condition
                                if (condition.mode == AnimatorConditionMode.If)
                                {
                                    // If our rule is configured for "If", then we need to check if our value expectation matches
                                    if (rule.ConditionMode == AnimatorConditionMode.If)
                                    {
                                        return ruleExpectsTrue;
                                    }
                                    // If our rule is configured for "IfNot", then we need to check the opposite
                                    else if (rule.ConditionMode == AnimatorConditionMode.IfNot)
                                    {
                                        return !ruleExpectsTrue;
                                    }
                                }
                                // For "IfNot" mode in the condition
                                else if (condition.mode == AnimatorConditionMode.IfNot)
                                {
                                    // If our rule is configured for "If", then we need to check the opposite
                                    if (rule.ConditionMode == AnimatorConditionMode.If)
                                    {
                                        return !ruleExpectsTrue;
                                    }
                                    // If our rule is configured for "IfNot", then we need to match the value expectation
                                    else if (rule.ConditionMode == AnimatorConditionMode.IfNot)
                                    {
                                        return ruleExpectsTrue;
                                    }
                                }
                            }
                            // For trigger parameters
                            else if (rule.ParameterType == AnimatorControllerParameterType.Trigger)
                            {
                                // For triggers, just check if condition mode matches rule condition mode
                                return condition.mode == rule.ConditionMode;
                            }
                            // For int/float parameters
                            else if (rule.ConditionMode == condition.mode)
                            {
                                // The threshold value should be close to rule value
                                float delta = Math.Abs(condition.threshold - rule.ThresholdValue);
                                float tolerance = rule.ParameterType == AnimatorControllerParameterType.Int ? 0.001f : 0.01f;

                                if (delta < tolerance)
                                    return true;
                            }
                        }
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Check if a transition is currently selected in the animator window
        /// </summary>
        private static bool IsTransitionSelected(AnimatorStateTransition transition)
        {
            try
            {
                // Check if the transition is in the current selection
                if (Selection.activeObject == transition)
                    return true;

                // Check if it's in the selection objects array
                if (Selection.objects != null)
                {
                    foreach (var obj in Selection.objects)
                    {
                        if (obj == transition)
                            return true;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        #region Animation Settings
        /// <summary>
        /// Get animation settings
        /// </summary>
        public static void GetAnimationSettings(out bool enableFlash, out float flashSpeed, out Color incomingColor, out Color outgoingColor, out float flashIntensity, out float sequenceDelay, out bool enableSequential)
        {
            enableFlash = _enableFlashAnimation;
            flashSpeed = _flashSpeed;
            incomingColor = _incomingFlashColor;
            outgoingColor = _outgoingFlashColor;
            flashIntensity = _flashIntensity;
            sequenceDelay = _sequenceDelay;
            enableSequential = _enableSequentialFlash;
        }

        /// <summary>
        /// Set animation settings
        /// </summary>
        public static void SetAnimationSettings(bool enableFlash, float flashSpeed, Color incomingColor, Color outgoingColor, float flashIntensity, float sequenceDelay, bool enableSequential)
        {
            _enableFlashAnimation = enableFlash;
            _flashSpeed = Mathf.Clamp(flashSpeed, 0.1f, 10f);
            _incomingFlashColor = incomingColor;
            _outgoingFlashColor = outgoingColor;
            _flashIntensity = Mathf.Clamp01(flashIntensity);
            _sequenceDelay = Mathf.Clamp(sequenceDelay, 0.0f, 5.0f);
            _enableSequentialFlash = enableSequential;

            SaveAnimationSettings();
        }

        /// <summary>
        /// Save animation settings to EditorPrefs
        /// </summary>
        private static void SaveAnimationSettings()
        {
            EditorPrefs.SetBool("SDKatHome.AnimatorTransition.EnableFlash", _enableFlashAnimation);
            EditorPrefs.SetFloat("SDKatHome.AnimatorTransition.FlashSpeed", _flashSpeed);
            EditorPrefs.SetString("SDKatHome.AnimatorTransition.IncomingFlashColor", ColorUtility.ToHtmlStringRGBA(_incomingFlashColor));
            EditorPrefs.SetString("SDKatHome.AnimatorTransition.OutgoingFlashColor", ColorUtility.ToHtmlStringRGBA(_outgoingFlashColor));
            EditorPrefs.SetFloat("SDKatHome.AnimatorTransition.FlashIntensity", _flashIntensity);
            EditorPrefs.SetFloat("SDKatHome.AnimatorTransition.SequenceDelay", _sequenceDelay);
            EditorPrefs.SetBool("SDKatHome.AnimatorTransition.EnableSequential", _enableSequentialFlash);
        }

        /// <summary>
        /// Load animation settings from EditorPrefs
        /// </summary>
        private static void LoadAnimationSettings()
        {
            _enableFlashAnimation = EditorPrefs.GetBool("SDKatHome.AnimatorTransition.EnableFlash", true);
            _flashSpeed = EditorPrefs.GetFloat("SDKatHome.AnimatorTransition.FlashSpeed", 2.0f);
            _flashIntensity = EditorPrefs.GetFloat("SDKatHome.AnimatorTransition.FlashIntensity", 0.8f);
            _sequenceDelay = EditorPrefs.GetFloat("SDKatHome.AnimatorTransition.SequenceDelay", 1.0f);
            _enableSequentialFlash = EditorPrefs.GetBool("SDKatHome.AnimatorTransition.EnableSequential", true);

            string incomingColorHex = EditorPrefs.GetString("SDKatHome.AnimatorTransition.IncomingFlashColor", "00FF00FF");
            if (!ColorUtility.TryParseHtmlString("#" + incomingColorHex, out _incomingFlashColor))
            {
                _incomingFlashColor = Color.green;
            }

            string outgoingColorHex = EditorPrefs.GetString("SDKatHome.AnimatorTransition.OutgoingFlashColor", "FF0000FF");
            if (!ColorUtility.TryParseHtmlString("#" + outgoingColorHex, out _outgoingFlashColor))
            {
                _outgoingFlashColor = Color.red;
            }
        }
        #endregion

        #region EditorPrefs Serialization
        /// <summary>
        /// Get the current color rules
        /// </summary>
        public static List<TransitionColorRule> GetColorRules()
        {
            // Make sure we've loaded rules first
            if (_colorRules.Count == 0)
            {
                LoadColorRules();
            }

            return _colorRules;
        }

        /// <summary>
        /// Set new color rules
        /// </summary>
        public static void SetColorRules(List<TransitionColorRule> rules)
        {
            _colorRules = rules ?? new List<TransitionColorRule>();
            SaveColorRules();
        }

        /// <summary>
        /// Save color rules to EditorPrefs
        /// </summary>
        public static void SaveColorRules()
        {
            try
            {
                string rulesJson = "";

                // Serialize each rule individually since Unity's JsonUtility doesn't handle lists well
                List<string> ruleJsons = new List<string>();
                foreach (var rule in _colorRules)
                {
                    // Make sure color hex is updated
                    rule.UpdateColorHex();

                    // Convert to JSON
                    string ruleJson = JsonUtility.ToJson(rule);
                    ruleJsons.Add(ruleJson);
                }

                // Join all rule JSONs with a separator
                rulesJson = string.Join("|||", ruleJsons);

                // Save to EditorPrefs
                EditorPrefs.SetString("SDKatHome.AnimatorTransitionColorRules", rulesJson);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SDK@Home] Error saving color rules: {ex.Message}");
            }
        }

        /// <summary>
        /// Load color rules from EditorPrefs
        /// </summary>
        public static void LoadColorRules()
        {
            try
            {
                // Get JSON from EditorPrefs
                string rulesJson = EditorPrefs.GetString("SDKatHome.AnimatorTransitionColorRules", "");

                if (!string.IsNullOrEmpty(rulesJson))
                {
                    // Parse the JSON
                    _colorRules.Clear();

                    // Split by separator
                    string[] ruleJsons = rulesJson.Split(new[] { "|||" }, StringSplitOptions.RemoveEmptyEntries);

                    // Parse each rule
                    foreach (string ruleJson in ruleJsons)
                    {
                        try
                        {
                            TransitionColorRule rule = JsonUtility.FromJson<TransitionColorRule>(ruleJson);

                            // Update color from hex to ensure it's loaded correctly
                            rule.UpdateColorFromHex();

                            _colorRules.Add(rule);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[SDK@Home] Error parsing rule JSON: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SDK@Home] Error loading color rules: {ex.Message}");
                _colorRules = new List<TransitionColorRule>();
            }
        }

        #endregion
    }

    /// <summary>
    /// Represents a rule for coloring transitions based on a parameter condition
    /// </summary>
    [Serializable]
    public class TransitionColorRule
    {
        public string ParameterName;
        public AnimatorControllerParameterType ParameterType;
        public AnimatorConditionMode ConditionMode;
        public float ThresholdValue;

        // Make Color serializable by using ColorUtility
        [SerializeField] private float _colorR, _colorG, _colorB, _colorA;
        public Color Color
        {
            get { return new Color(_colorR, _colorG, _colorB, _colorA); }
            set { _colorR = value.r; _colorG = value.g; _colorB = value.b; _colorA = value.a; UpdateColorHex(); }
        }

        // For serialization purposes
        public string ColorHex;

        public TransitionColorRule()
        {
            ParameterName = "New Parameter";
            ParameterType = AnimatorControllerParameterType.Bool;
            ConditionMode = AnimatorConditionMode.If;
            ThresholdValue = 0f;
            Color = Color.yellow;
            UpdateColorHex();
        }

        public void UpdateColorHex()
        {
            ColorHex = ColorUtility.ToHtmlStringRGBA(Color);
        }

        public void UpdateColorFromHex()
        {
            if (!string.IsNullOrEmpty(ColorHex))
            {
                Color tempColor;
                if (ColorUtility.TryParseHtmlString("#" + ColorHex, out tempColor))
                {
                    _colorR = tempColor.r;
                    _colorG = tempColor.g;
                    _colorB = tempColor.b;
                    _colorA = tempColor.a;
                }
            }
            else
            {
                Color = Color.yellow; // Default
            }
        }
    }

    /// <summary>
    /// Wrapper class for serializing a list of TransitionColorRule
    /// </summary>
    [Serializable]
    public class ColorRulesWrapper
    {
        public List<TransitionColorRule> rules;

        public ColorRulesWrapper(List<TransitionColorRule> rules)
        {
            this.rules = rules;
        }
    }
}
#endif