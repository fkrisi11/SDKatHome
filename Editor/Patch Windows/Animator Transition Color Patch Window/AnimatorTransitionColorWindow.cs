#if UNITY_EDITOR
using SDKatHome.Patches;
using System.Collections.Generic;
using System.Reflection;
using System;
using System.Linq;
using UnityEditor.Animations;
using UnityEditor;
using UnityEngine;

public class AnimatorTransitionColorWindow : EditorWindow
{
    [SerializeField] // Add this to serialize changes for undo
    private List<TransitionColorRule> _colorRules;
    private Vector2 _rulesScrollPosition;
    private Vector2 _parametersScrollPosition;
    private AnimatorController _currentController;
    private bool _showParameters = true;
    private string _parameterSearch = "";

    // Multi-selection support
    private List<int> _selectedIndices = new List<int>();
    private int _lastSelectedIndex = -1; // Track last selected index for shift selection

    // Expanded/Collapsed state tracking
    private HashSet<int> _expandedRules = new HashSet<int>();

    // UI styling
    private GUIStyle _ruleHeaderStyle;
    private GUIStyle _duplicateRuleStyle;
    private GUIStyle _selectedRuleStyle;

    // Heights
    private const float HEADER_HEIGHT = 24f;
    private const float EXPANDED_RULE_HEIGHT = 140f;
    private const float COMPACT_RULE_HEIGHT = 60f;
    private const float RULE_SPACING = 4f;
    private const float FIELD_SPACING = 5f;
    private const float FIELD_HEIGHT = 18f;

    // Animation settings
    [SerializeField]
    private bool _enableFlashAnimation = true;

    [SerializeField]
    private float _flashSpeed = 1.0f;

    [SerializeField]
    private Color _incomingFlashColor = Color.blue;

    [SerializeField]
    private Color _outgoingFlashColor = Color.cyan;

    [SerializeField]
    private float _flashIntensity = 0.8f;

    [SerializeField]
    private float _sequenceDelay = 0.0f;

    [SerializeField]
    private bool _enableSequentialFlash = true;

    // Tab system
    private enum WindowTab
    {
        Rules,
        Settings
    }

    private WindowTab _currentTab = WindowTab.Rules;

    public static void ShowWindow()
    {
        AnimatorTransitionColorWindow window = GetWindow<AnimatorTransitionColorWindow>();
        window.titleContent = new GUIContent("Transition Colors");
        window.minSize = new Vector2(450, 300);
        window.Show();
    }

    private void OnEnable()
    {
        // Load color rules
        _colorRules = AnimatorTransitionColorPatch.GetColorRules();

        // Initialize styles
        InitializeStyles();

        // Register undo callback
        Undo.undoRedoPerformed += OnUndoRedo;

        AnimatorTransitionColorPatch.GetAnimationSettings(
            out _enableFlashAnimation,
            out _flashSpeed,
            out _incomingFlashColor,
            out _outgoingFlashColor,
            out _flashIntensity,
            out _sequenceDelay,
            out _enableSequentialFlash);

        LoadUISettings();
    }

    private void OnDisable()
    {
        // Remove undo callback when window is closed
        Undo.undoRedoPerformed -= OnUndoRedo;
    }

    private void OnUndoRedo()
    {
        // When undo/redo happens, Unity restores our _colorRules field and animation settings
        // We need to sync this back to the patch system and ensure colors are correct

        if (_colorRules != null)
        {
            // Ensure all colors are properly restored from the serialized data
            foreach (var rule in _colorRules)
            {
                // Force color consistency - this ensures the Color property reflects the serialized values
                rule.UpdateColorFromHex();
                rule.UpdateColorHex();
            }

            // Update the patch with the restored rules
            AnimatorTransitionColorPatch.SetColorRules(_colorRules);
        }

        // Restore animation settings to the patch
        AnimatorTransitionColorPatch.SetAnimationSettings(
            _enableFlashAnimation, _flashSpeed, _incomingFlashColor, _outgoingFlashColor,
            _flashIntensity, _sequenceDelay, _enableSequentialFlash);

        // Repaint to show the restored state
        Repaint();
    }

    private void InitializeStyles()
    {
        _ruleHeaderStyle = new GUIStyle(EditorStyles.foldout);
        _ruleHeaderStyle.fontStyle = FontStyle.Bold;

        _duplicateRuleStyle = new GUIStyle(EditorStyles.label);
        _duplicateRuleStyle.normal.textColor = Color.red;

        _selectedRuleStyle = new GUIStyle(EditorStyles.helpBox);
        _selectedRuleStyle.normal.background = EditorGUIUtility.whiteTexture;
    }

    private void OnGUI()
    {
        Event currentEvent = Event.current;

        // Handle keyboard events for moving selected items
        HandleKeyboardEvents();

        // Draw the header area with tabs
        Rect headerArea = new Rect(0, 0, position.width, 130); // Increased height for tabs
        GUILayout.BeginArea(headerArea);

        // Title
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Animator Transition Color Rules", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        // Tab bar
        EditorGUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();

        // Tab buttons with custom styling
        GUIStyle tabStyle = new GUIStyle(EditorStyles.miniButton);
        GUIStyle activeTabStyle = new GUIStyle(EditorStyles.miniButton);
        activeTabStyle.normal.background = activeTabStyle.active.background;

        if (GUILayout.Toggle(_currentTab == WindowTab.Rules, "Rules",
            _currentTab == WindowTab.Rules ? activeTabStyle : tabStyle, GUILayout.Width(80)))
        {
            _currentTab = WindowTab.Rules;
        }

        if (GUILayout.Toggle(_currentTab == WindowTab.Settings, "Settings",
            _currentTab == WindowTab.Settings ? activeTabStyle : tabStyle, GUILayout.Width(80)))
        {
            _currentTab = WindowTab.Settings;
        }

        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();

        // Get current Animator Controller (shown on both tabs)
        _currentController = GetAnimatorController();

        if (_currentController != null)
        {
            EditorGUILayout.LabelField($"Current Animator: {_currentController.name}", EditorStyles.boldLabel);
        }
        else
        {
            EditorGUILayout.LabelField("No Animator Controller selected or active.");
        }

        EditorGUILayout.Space();

        GUILayout.EndArea();

        // Content area based on selected tab
        Rect contentArea = new Rect(0, headerArea.height, position.width, position.height - headerArea.height);

        switch (_currentTab)
        {
            case WindowTab.Rules:
                DrawRulesTab(contentArea, currentEvent);
                break;
            case WindowTab.Settings:
                DrawSettingsTab(contentArea);
                break;
        }
    }

    private void DrawRulesTab(Rect contentArea, Event currentEvent)
    {
        GUILayout.BeginArea(contentArea);

        // Rules section header
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField($"Color Rules ({_colorRules?.Count ?? 0})", EditorStyles.boldLabel);

        GUILayout.FlexibleSpace();

        if (GUILayout.Button("Add New Rule", GUILayout.Width(120)))
        {
            RecordUndoAndApplyChanges("Add Rule", () =>
            {
                var newRule = new TransitionColorRule
                {
                    Color = GetRandomColor()
                };
                _colorRules.Add(newRule);

                // Auto-select the new rule
                _selectedIndices.Clear();
                _selectedIndices.Add(_colorRules.Count - 1);

                // Auto-expand the new rule
                _expandedRules.Add(_colorRules.Count - 1);
            });
        }
        EditorGUILayout.EndHorizontal();

        // Rest of the method remains the same...
        EditorGUILayout.Space();
        GUILayout.EndArea();

        // Calculate better layout - split the content area between rules and parameters
        float rulesHeaderHeight = 50f;
        float parametersHeaderHeight = 30f;
        float parametersMinHeight = 120f;

        float availableHeight = contentArea.height - rulesHeaderHeight;
        float rulesHeight = Mathf.Max(200f, availableHeight - parametersMinHeight - parametersHeaderHeight);
        float parametersHeight = availableHeight - rulesHeight;

        Rect scrollArea = new Rect(0, contentArea.y + rulesHeaderHeight, contentArea.width, rulesHeight);

        var duplicateRules = FindDuplicateRules();

        float totalContentHeight = 0;
        for (int i = 0; i < _colorRules.Count; i++)
        {
            bool isExpanded = _expandedRules.Contains(i);
            totalContentHeight += (isExpanded ? EXPANDED_RULE_HEIGHT : COMPACT_RULE_HEIGHT) + RULE_SPACING;
        }

        Rect viewRect = new Rect(0, 0, scrollArea.width - 20, totalContentHeight);
        _rulesScrollPosition = GUI.BeginScrollView(scrollArea, _rulesScrollPosition, viewRect);

        DrawRulesList(viewRect, currentEvent, duplicateRules);

        GUI.EndScrollView();

        Rect parametersArea = new Rect(0, scrollArea.yMax, contentArea.width, parametersHeight);
        DrawParametersSection(parametersArea);
    }

    private void DrawSettingsTab(Rect contentArea)
    {
        GUILayout.BeginArea(contentArea);

        EditorGUILayout.Space();

        _parametersScrollPosition = EditorGUILayout.BeginScrollView(_parametersScrollPosition);

        // Animation Settings Section
        EditorGUILayout.LabelField("Animation Settings", EditorStyles.boldLabel);
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        // Enable flash animation toggle - WITH UNDO
        EditorGUI.BeginChangeCheck();
        bool newEnableFlash = EditorGUILayout.Toggle(
            new GUIContent("Enable Flash Animation", "Highlights transitions connected to the selected state"),
            _enableFlashAnimation);
        if (EditorGUI.EndChangeCheck() && newEnableFlash != _enableFlashAnimation)
        {
            RecordUndoAndApplyChanges("Toggle Flash Animation", () =>
            {
                _enableFlashAnimation = newEnableFlash;
                AnimatorTransitionColorPatch.SetAnimationSettings(
                    _enableFlashAnimation, _flashSpeed, _incomingFlashColor, _outgoingFlashColor,
                    _flashIntensity, _sequenceDelay, _enableSequentialFlash);
            });
        }

        if (_enableFlashAnimation)
        {
            EditorGUI.indentLevel++;

            // Sequential vs Simultaneous mode - WITH UNDO
            EditorGUI.BeginChangeCheck();
            bool newSequentialFlash = EditorGUILayout.Toggle(
                new GUIContent("Sequential Flash", "Flash incoming transitions first, then outgoing. Uncheck for simultaneous flashing."),
                _enableSequentialFlash);
            if (EditorGUI.EndChangeCheck() && newSequentialFlash != _enableSequentialFlash)
            {
                RecordUndoAndApplyChanges("Toggle Sequential Flash", () =>
                {
                    _enableSequentialFlash = newSequentialFlash;
                    AnimatorTransitionColorPatch.SetAnimationSettings(
                        _enableFlashAnimation, _flashSpeed, _incomingFlashColor, _outgoingFlashColor,
                        _flashIntensity, _sequenceDelay, _enableSequentialFlash);
                });
            }

            EditorGUILayout.Space();

            // Flash speed - WITH UNDO
            EditorGUI.BeginChangeCheck();
            float newFlashSpeed = EditorGUILayout.Slider(
                new GUIContent("Flash Speed", "How fast the transitions pulse (0.1x to 10x speed)"),
                _flashSpeed, 0.1f, 10f);
            if (EditorGUI.EndChangeCheck() && Math.Abs(newFlashSpeed - _flashSpeed) > 0.001f)
            {
                RecordUndoAndApplyChanges("Change Flash Speed", () =>
                {
                    _flashSpeed = newFlashSpeed;
                    AnimatorTransitionColorPatch.SetAnimationSettings(
                        _enableFlashAnimation, _flashSpeed, _incomingFlashColor, _outgoingFlashColor,
                        _flashIntensity, _sequenceDelay, _enableSequentialFlash);
                });
            }

            EditorGUILayout.Space();

            // Color settings
            EditorGUILayout.LabelField("Flash Colors", EditorStyles.boldLabel);

            // Incoming color - WITH UNDO
            EditorGUI.BeginChangeCheck();
            Color newIncomingColor = EditorGUILayout.ColorField(
                new GUIContent("Incoming Transitions", "Color for transitions leading TO the selected state"),
                _incomingFlashColor);
            if (EditorGUI.EndChangeCheck() && newIncomingColor != _incomingFlashColor)
            {
                RecordUndoAndApplyChanges("Change Incoming Flash Color", () =>
                {
                    _incomingFlashColor = newIncomingColor;
                    AnimatorTransitionColorPatch.SetAnimationSettings(
                        _enableFlashAnimation, _flashSpeed, _incomingFlashColor, _outgoingFlashColor,
                        _flashIntensity, _sequenceDelay, _enableSequentialFlash);
                });
            }

            // Outgoing color - WITH UNDO
            EditorGUI.BeginChangeCheck();
            Color newOutgoingColor = EditorGUILayout.ColorField(
                new GUIContent("Outgoing Transitions", "Color for transitions leading FROM the selected state"),
                _outgoingFlashColor);
            if (EditorGUI.EndChangeCheck() && newOutgoingColor != _outgoingFlashColor)
            {
                RecordUndoAndApplyChanges("Change Outgoing Flash Color", () =>
                {
                    _outgoingFlashColor = newOutgoingColor;
                    AnimatorTransitionColorPatch.SetAnimationSettings(
                        _enableFlashAnimation, _flashSpeed, _incomingFlashColor, _outgoingFlashColor,
                        _flashIntensity, _sequenceDelay, _enableSequentialFlash);
                });
            }

            if (_enableSequentialFlash)
            {
                EditorGUILayout.Space();

                // Sequence delay
                EditorGUI.BeginChangeCheck();
                float newSequenceDelay = EditorGUILayout.Slider(
                    new GUIContent("Cycle Delay", "Pause duration after each complete cycle (seconds). Can be 0 for continuous flashing."),
                    _sequenceDelay, 0.0f, 5.0f);
                if (EditorGUI.EndChangeCheck() && Math.Abs(newSequenceDelay - _sequenceDelay) > 0.001f)
                {
                    RecordUndoAndApplyChanges("Change Sequence Delay", () =>
                    {
                        _sequenceDelay = newSequenceDelay;
                        AnimatorTransitionColorPatch.SetAnimationSettings(
                            _enableFlashAnimation, _flashSpeed, _incomingFlashColor, _outgoingFlashColor,
                            _flashIntensity, _sequenceDelay, _enableSequentialFlash);
                    });
                }
            }

            EditorGUI.indentLevel--;

            EditorGUILayout.Space();

            // Help text based on mode
            if (_enableSequentialFlash)
            {
                EditorGUILayout.HelpBox(
                    "Sequential Mode: Incoming transitions flash, then immediately outgoing transitions flash, then pause (if delay > 0), then repeat. " +
                    "This creates a rapid sequence pattern: IN → OUT → pause → IN → OUT → pause. Set Cycle Delay to 0 for continuous back-to-back flashing.",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Simultaneous Mode: Both incoming and outgoing transitions continuously flash at the same time with different colors. " +
                    "Good for seeing all connections at once.",
                    MessageType.Info);
            }
        }

        EditorGUILayout.EndVertical();

        EditorGUILayout.Space();

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        if (GUILayout.Button("Reset All Settings"))
        {
            if (EditorUtility.DisplayDialog("Reset Settings",
                "Are you sure you want to reset all settings to their default values?",
                "Reset", "Cancel"))
            {
                ResetAllSettings();
            }
        }

        EditorGUILayout.Space();

        if (GUILayout.Button("Export Settings"))
        {
            ExportSettings();
        }

        if (GUILayout.Button("Import Settings"))
        {
            ImportSettings();
        }

        EditorGUILayout.EndVertical();

        EditorGUILayout.EndScrollView();

        GUILayout.EndArea();
    }

    private void DrawRulesList(Rect viewRect, Event currentEvent, HashSet<int> duplicateRules)
    {
        float yPosition = 0;

        for (int i = 0; i < _colorRules.Count; i++)
        {
            TransitionColorRule rule = _colorRules[i];
            bool isDuplicate = duplicateRules.Contains(i);
            bool isSelected = _selectedIndices.Contains(i);
            bool isExpanded = _expandedRules.Contains(i);

            float ruleHeight = isExpanded ? EXPANDED_RULE_HEIGHT : COMPACT_RULE_HEIGHT;

            // Draw rule background
            Rect ruleRect = new Rect(5, yPosition, viewRect.width - 10, ruleHeight);

            // Better selection color handling
            if (isSelected)
            {
                Color selectionColor = new Color(0.24f, 0.48f, 0.90f, 1f);
                Color oldColor = GUI.backgroundColor;
                GUI.backgroundColor = selectionColor;
                GUI.Box(ruleRect, "", EditorStyles.helpBox);
                GUI.backgroundColor = oldColor;
            }
            else
            {
                GUI.Box(ruleRect, "", EditorStyles.helpBox);
            }

            // Calculate layout positions
            Rect headerRect = new Rect(ruleRect.x + 5, ruleRect.y + 5, ruleRect.width - 10, HEADER_HEIGHT);
            Rect selectionRect = new Rect(ruleRect.x, ruleRect.y, ruleRect.width - 110, ruleRect.height);

            float buttonSectionWidth = 100;
            float colorFieldWidth = 60;
            float valueFieldWidth = 40;
            float controlSpacing = 5;

            float rightSectionX = ruleRect.xMax - buttonSectionWidth - 10;
            float actionButtonWidth = 28;
            float actionButtonSpacing = 5;
            float actionButtonStartX = rightSectionX;

            float colorFieldX = rightSectionX - colorFieldWidth - controlSpacing;
            Rect colorRect = new Rect(colorFieldX, headerRect.y + 2, colorFieldWidth, headerRect.height - 4);

            float valueRectX = colorFieldX - valueFieldWidth - controlSpacing;
            Rect valueRect = new Rect(valueRectX, headerRect.y + 2, valueFieldWidth, headerRect.height - 4);

            Rect moveUpRect = new Rect(actionButtonStartX, headerRect.y, actionButtonWidth, headerRect.height);
            Rect moveDownRect = new Rect(actionButtonStartX + actionButtonWidth + actionButtonSpacing, headerRect.y, actionButtonWidth, headerRect.height);
            Rect deleteRect = new Rect(actionButtonStartX + (actionButtonWidth + actionButtonSpacing) * 2, headerRect.y, actionButtonWidth, headerRect.height);
            Rect foldoutRect = new Rect(headerRect.x, headerRect.y, 20, headerRect.height);

            // Calculate expanded mode rects
            Rect nameFieldRect = Rect.zero;
            Rect typeFieldRect = Rect.zero;
            Rect modeFieldRect = Rect.zero;
            Rect valueFieldExpandedRect = Rect.zero;

            if (isExpanded)
            {
                Rect contentRect = new Rect(ruleRect.x + 20, headerRect.yMax + 5, ruleRect.width - 40, ruleRect.height - headerRect.height - 10);
                float fieldY = contentRect.y;

                nameFieldRect = new Rect(contentRect.x + 130, fieldY, contentRect.width - 140, FIELD_HEIGHT);
                fieldY += FIELD_HEIGHT + FIELD_SPACING;

                typeFieldRect = new Rect(contentRect.x + 130, fieldY, contentRect.width - 140, FIELD_HEIGHT);
                fieldY += FIELD_HEIGHT + FIELD_SPACING;

                modeFieldRect = new Rect(contentRect.x + 130, fieldY, contentRect.width - 140, FIELD_HEIGHT);
                fieldY += FIELD_HEIGHT + FIELD_SPACING;

                if (rule.ParameterType != AnimatorControllerParameterType.Trigger)
                {
                    valueFieldExpandedRect = new Rect(contentRect.x + 130, fieldY, contentRect.width - 140, FIELD_HEIGHT);
                }
            }

            // Foldout
            bool newExpanded = EditorGUI.Foldout(foldoutRect, isExpanded, "", true);
            if (newExpanded != isExpanded)
            {
                if (newExpanded)
                    _expandedRules.Add(i);
                else
                    _expandedRules.Remove(i);
                currentEvent.Use();
            }

            // Selection handling
            if (currentEvent.type == EventType.MouseDown &&
                currentEvent.button == 0 &&
                selectionRect.Contains(currentEvent.mousePosition) &&
                !foldoutRect.Contains(currentEvent.mousePosition) &&
                !colorRect.Contains(currentEvent.mousePosition) &&
                !valueRect.Contains(currentEvent.mousePosition) &&
                !moveUpRect.Contains(currentEvent.mousePosition) &&
                !moveDownRect.Contains(currentEvent.mousePosition) &&
                !deleteRect.Contains(currentEvent.mousePosition) &&
                !nameFieldRect.Contains(currentEvent.mousePosition) &&
                !typeFieldRect.Contains(currentEvent.mousePosition) &&
                !modeFieldRect.Contains(currentEvent.mousePosition) &&
                !valueFieldExpandedRect.Contains(currentEvent.mousePosition))
            {
                HandleRuleSelection(i, currentEvent.control || currentEvent.shift);
                currentEvent.Use();
            }

            // Rule text
            float textRectWidth = valueRectX - foldoutRect.xMax - 10;
            Rect textRect = new Rect(foldoutRect.xMax + 5, headerRect.y, textRectWidth, headerRect.height);
            string displayText = GetCompactRuleDisplay(rule);
            GUIStyle textStyle = isDuplicate ? _duplicateRuleStyle : EditorStyles.boldLabel;
            GUI.Label(textRect, displayText, textStyle);

            EditorGUI.BeginChangeCheck();
            Color newColor = EditorGUI.ColorField(colorRect, rule.Color);
            if (EditorGUI.EndChangeCheck() && newColor != rule.Color)
            {
                RecordUndoAndApplyChanges("Change Color", () =>
                {
                    rule.Color = newColor;
                    rule.UpdateColorHex();
                });
                currentEvent.Use();
            }

            if (rule.ParameterType != AnimatorControllerParameterType.Trigger)
            {
                EditorGUI.BeginChangeCheck();

                if (rule.ParameterType == AnimatorControllerParameterType.Bool)
                {
                    bool currentValue = rule.ThresholdValue >= 0.5f;
                    bool newValue = EditorGUI.Toggle(valueRect, currentValue);
                    if (EditorGUI.EndChangeCheck() && newValue != currentValue)
                    {
                        RecordUndoAndApplyChanges("Change Value", () =>
                        {
                            rule.ThresholdValue = newValue ? 1f : 0f;
                        });
                        currentEvent.Use();
                    }
                }
                else if (rule.ParameterType == AnimatorControllerParameterType.Int)
                {
                    int newValue = EditorGUI.IntField(valueRect, (int)rule.ThresholdValue);
                    if (EditorGUI.EndChangeCheck() && newValue != (int)rule.ThresholdValue)
                    {
                        RecordUndoAndApplyChanges("Change Value", () =>
                        {
                            rule.ThresholdValue = newValue;
                        });
                        currentEvent.Use();
                    }
                }
                else
                {
                    float newValue = EditorGUI.FloatField(valueRect, rule.ThresholdValue);
                    if (EditorGUI.EndChangeCheck() && Math.Abs(newValue - rule.ThresholdValue) > 0.0001f)
                    {
                        RecordUndoAndApplyChanges("Change Value", () =>
                        {
                            rule.ThresholdValue = newValue;
                        });
                        currentEvent.Use();
                    }
                }
            }

            if (GUI.Button(moveUpRect, "▲") && CanMoveUp(i))
            {
                RecordUndoAndApplyChanges("Move Rule Up", () =>
                {
                    List<int> indicesToMove = _selectedIndices.Contains(i) ? new List<int>(_selectedIndices) : new List<int> { i };
                    MoveRulesUp(indicesToMove);
                });
                currentEvent.Use();
            }

            if (GUI.Button(moveDownRect, "▼") && CanMoveDown(i))
            {
                RecordUndoAndApplyChanges("Move Rule Down", () =>
                {
                    List<int> indicesToMove = _selectedIndices.Contains(i) ? new List<int>(_selectedIndices) : new List<int> { i };
                    MoveRulesDown(indicesToMove);
                });
                currentEvent.Use();
            }

            if (GUI.Button(deleteRect, "X"))
            {
                if (_selectedIndices.Contains(i) && _selectedIndices.Count > 1)
                {
                    RecordUndoAndApplyChanges("Delete Selected Rules", () =>
                    {
                        DeleteSelectedRulesInternal();
                    });
                }
                else
                {
                    RecordUndoAndApplyChanges("Delete Rule", () =>
                    {
                        _colorRules.RemoveAt(i);

                        // Update selected indices
                        _selectedIndices = _selectedIndices.Select(idx => idx > i ? idx - 1 : idx)
                                                          .Where(idx => idx >= 0 && idx < _colorRules.Count)
                                                          .ToList();

                        // Update expanded rules
                        _expandedRules.RemoveWhere(x => x == i);
                        var expandedToAdjust = _expandedRules.Where(x => x > i).ToList();
                        foreach (var idx in expandedToAdjust)
                        {
                            _expandedRules.Remove(idx);
                            _expandedRules.Add(idx - 1);
                        }
                    });
                    i--;
                    continue;
                }
                currentEvent.Use();
            }

            if (isExpanded)
            {
                Rect contentRect = new Rect(ruleRect.x + 20, headerRect.yMax + 5, ruleRect.width - 40, ruleRect.height - headerRect.height - 10);
                float fieldY = contentRect.y;

                // Parameter name
                Rect nameLabel = new Rect(contentRect.x, fieldY, 120, FIELD_HEIGHT);
                GUI.Label(nameLabel, "Parameter Name");

                EditorGUI.BeginChangeCheck();
                string newName = EditorGUI.TextField(nameFieldRect, rule.ParameterName);
                if (EditorGUI.EndChangeCheck() && newName != rule.ParameterName)
                {
                    RecordUndoAndApplyChanges("Change Parameter Name", () =>
                    {
                        rule.ParameterName = newName;
                    });
                }
                fieldY += FIELD_HEIGHT + FIELD_SPACING;

                // Parameter type
                Rect typeLabel = new Rect(contentRect.x, fieldY, 120, FIELD_HEIGHT);
                GUI.Label(typeLabel, "Parameter Type");

                EditorGUI.BeginChangeCheck();
                AnimatorControllerParameterType newType = (AnimatorControllerParameterType)EditorGUI.EnumPopup(typeFieldRect, rule.ParameterType);
                if (EditorGUI.EndChangeCheck() && newType != rule.ParameterType)
                {
                    RecordUndoAndApplyChanges("Change Parameter Type", () =>
                    {
                        rule.ParameterType = newType;
                    });
                }
                fieldY += FIELD_HEIGHT + FIELD_SPACING;

                // Condition mode
                Rect modeLabel = new Rect(contentRect.x, fieldY, 120, FIELD_HEIGHT);
                GUI.Label(modeLabel, "Condition Mode");

                AnimatorConditionMode[] availableModes = GetAvailableConditionModes(rule.ParameterType);
                int modeIndex = Array.IndexOf(availableModes, rule.ConditionMode);
                if (modeIndex < 0) modeIndex = 0;

                EditorGUI.BeginChangeCheck();
                modeIndex = EditorGUI.Popup(modeFieldRect, modeIndex, Array.ConvertAll(availableModes, m => m.ToString()));
                if (EditorGUI.EndChangeCheck() && availableModes[modeIndex] != rule.ConditionMode)
                {
                    RecordUndoAndApplyChanges("Change Condition Mode", () =>
                    {
                        rule.ConditionMode = availableModes[modeIndex];
                    });
                }
                fieldY += FIELD_HEIGHT + FIELD_SPACING;

                // Value field
                if (rule.ParameterType != AnimatorControllerParameterType.Trigger)
                {
                    Rect valueLabel = new Rect(contentRect.x, fieldY, 120, FIELD_HEIGHT);
                    GUI.Label(valueLabel, "Value");

                    EditorGUI.BeginChangeCheck();

                    if (rule.ParameterType == AnimatorControllerParameterType.Int)
                    {
                        int newValue = EditorGUI.IntField(valueFieldExpandedRect, (int)rule.ThresholdValue);
                        if (EditorGUI.EndChangeCheck() && newValue != (int)rule.ThresholdValue)
                        {
                            RecordUndoAndApplyChanges("Change Value", () =>
                            {
                                rule.ThresholdValue = newValue;
                            });
                        }
                    }
                    else if (rule.ParameterType == AnimatorControllerParameterType.Bool)
                    {
                        Rect toggleField = new Rect(valueFieldExpandedRect.x, valueFieldExpandedRect.y, 20, valueFieldExpandedRect.height);
                        bool currentValue = rule.ThresholdValue >= 0.5f;
                        bool newValue = EditorGUI.Toggle(toggleField, currentValue);
                        if (EditorGUI.EndChangeCheck() && newValue != currentValue)
                        {
                            RecordUndoAndApplyChanges("Change Value", () =>
                            {
                                rule.ThresholdValue = newValue ? 1f : 0f;
                            });
                        }
                    }
                    else
                    {
                        float newValue = EditorGUI.FloatField(valueFieldExpandedRect, rule.ThresholdValue);
                        if (EditorGUI.EndChangeCheck() && Math.Abs(newValue - rule.ThresholdValue) > 0.0001f)
                        {
                            RecordUndoAndApplyChanges("Change Value", () =>
                            {
                                rule.ThresholdValue = newValue;
                            });
                        }
                    }
                }
            }

            yPosition += ruleHeight + RULE_SPACING;
        }
    }

    private void HandleKeyboardEvents()
    {
        Event currentEvent = Event.current;

        if (currentEvent.type == EventType.KeyDown && _selectedIndices.Count > 0)
        {
            bool handled = false;

            if (currentEvent.keyCode == KeyCode.UpArrow)
            {
                if (CanMoveUp(_selectedIndices[0]))
                {
                    RecordUndoAndApplyChanges("Move Rules Up", () =>
                    {
                        MoveRulesUp(new List<int>(_selectedIndices));
                    });
                    handled = true;
                }
            }
            else if (currentEvent.keyCode == KeyCode.DownArrow)
            {
                if (CanMoveDown(_selectedIndices[_selectedIndices.Count - 1]))
                {
                    RecordUndoAndApplyChanges("Move Rules Down", () =>
                    {
                        MoveRulesDown(new List<int>(_selectedIndices));
                    });
                    handled = true;
                }
            }

            if (handled)
            {
                currentEvent.Use();
                Repaint();
            }
        }
    }

    private bool CanMoveUp(int index)
    {
        if (_selectedIndices.Contains(index))
        {
            // For multi-selection, check if any selected item can move up
            return _selectedIndices.Min() > 0;
        }
        else
        {
            // For single item, just check if it can move up
            return index > 0;
        }
    }

    private bool CanMoveDown(int index)
    {
        if (_selectedIndices.Contains(index))
        {
            // For multi-selection, check if any selected item can move down
            return _selectedIndices.Max() < _colorRules.Count - 1;
        }
        else
        {
            // For single item, just check if it can move down
            return index < _colorRules.Count - 1;
        }
    }

    private void MoveRulesUp(List<int> indices)
    {
        if (indices.Count == 0) return;

        // Sort indices in ascending order
        indices.Sort();

        // Check if we can move all selected items up
        if (indices[0] <= 0) return;

        // Clear and rebuild selection tracking
        _selectedIndices.Clear();

        // Move each rule up and update selection
        foreach (int index in indices)
        {
            if (index > 0)
            {
                // Swap rules
                TransitionColorRule temp = _colorRules[index];
                _colorRules[index] = _colorRules[index - 1];
                _colorRules[index - 1] = temp;

                // Update selection to new position
                _selectedIndices.Add(index - 1);

                // Update expanded state
                bool wasExpanded = _expandedRules.Contains(index);
                bool targetWasExpanded = _expandedRules.Contains(index - 1);

                _expandedRules.Remove(index);
                _expandedRules.Remove(index - 1);

                if (wasExpanded) _expandedRules.Add(index - 1);
                if (targetWasExpanded) _expandedRules.Add(index);
            }
        }

        // Update last selected index
        if (_selectedIndices.Count > 0)
        {
            _lastSelectedIndex = _selectedIndices[_selectedIndices.Count - 1];
        }
    }

    private void MoveRulesDown(List<int> indices)
    {
        if (indices.Count == 0) return;

        // Sort indices in descending order for moving down
        indices.Sort((a, b) => b.CompareTo(a));

        // Check if we can move all selected items down
        if (indices[0] >= _colorRules.Count - 1) return;

        // Clear and rebuild selection tracking
        _selectedIndices.Clear();

        // Move each rule down and update selection
        foreach (int index in indices)
        {
            if (index < _colorRules.Count - 1)
            {
                // Swap rules
                TransitionColorRule temp = _colorRules[index];
                _colorRules[index] = _colorRules[index + 1];
                _colorRules[index + 1] = temp;

                // Update selection to new position
                _selectedIndices.Add(index + 1);

                // Update expanded state
                bool wasExpanded = _expandedRules.Contains(index);
                bool targetWasExpanded = _expandedRules.Contains(index + 1);

                _expandedRules.Remove(index);
                _expandedRules.Remove(index + 1);

                if (wasExpanded) _expandedRules.Add(index + 1);
                if (targetWasExpanded) _expandedRules.Add(index);
            }
        }

        // Sort selection indices and update last selected
        _selectedIndices.Sort();
        if (_selectedIndices.Count > 0)
        {
            _lastSelectedIndex = _selectedIndices[_selectedIndices.Count - 1];
        }
    }

    private void DrawParametersSection(Rect parametersArea)
    {
        GUILayout.BeginArea(parametersArea);

        if (_currentController != null)
        {
            EditorGUILayout.BeginHorizontal();
            _showParameters = EditorGUILayout.Foldout(_showParameters, $"Available Parameters ({_currentController.parameters?.Length ?? 0})", true);

            GUILayout.FlexibleSpace();

            GUILayout.Label("Search:", GUILayout.Width(50));
            _parameterSearch = EditorGUILayout.TextField(_parameterSearch, GUILayout.Width(150));

            if (GUILayout.Button("Clear", GUILayout.Width(60)))
            {
                _parameterSearch = "";
                GUI.FocusControl(null);
            }
            EditorGUILayout.EndHorizontal();

            if (_showParameters)
            {
                float headerHeight = 30f;
                float availableScrollHeight = parametersArea.height - headerHeight - 10f;

                Vector2 paramScrollPos = EditorGUILayout.BeginScrollView(
                    _parametersScrollPosition,
                    GUILayout.Height(availableScrollHeight)
                );
                _parametersScrollPosition = paramScrollPos;

                var parameters = _currentController.parameters;

                if (parameters == null || parameters.Length == 0)
                {
                    EditorGUILayout.LabelField("No parameters found in the current animator controller.");
                }
                else
                {
                    var filteredParams = string.IsNullOrEmpty(_parameterSearch)
                        ? parameters
                        : parameters.Where(p => p.name.IndexOf(_parameterSearch, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();

                    if (filteredParams.Length == 0)
                    {
                        EditorGUILayout.LabelField($"No parameters match your search");
                    }
                    else
                    {
                        foreach (var parameter in filteredParams)
                        {
                            EditorGUILayout.BeginHorizontal();

                            EditorGUILayout.LabelField($"{parameter.name} ({parameter.type})", GUILayout.ExpandWidth(true));

                            if (GUILayout.Button("Add Rule", GUILayout.Width(80)))
                            {
                                RecordUndoAndApplyChanges("Add Rule From Parameter", () =>
                                {
                                    AddRuleFromParameterInternal(parameter);
                                });
                            }

                            EditorGUILayout.EndHorizontal();
                        }
                    }
                }

                EditorGUILayout.EndScrollView();
            }
        }

        GUILayout.EndArea();
    }

    private void DeleteSelectedRulesInternal()
    {
        if (_selectedIndices.Count == 0)
            return;

        var sortedIndices = new List<int>(_selectedIndices);
        sortedIndices.Sort((a, b) => b.CompareTo(a));

        foreach (int index in sortedIndices)
        {
            if (index >= 0 && index < _colorRules.Count)
            {
                _colorRules.RemoveAt(index);
                _expandedRules.RemoveWhere(x => x == index);

                var expandedToAdjust = _expandedRules.Where(x => x > index).ToList();
                foreach (var idx in expandedToAdjust)
                {
                    _expandedRules.Remove(idx);
                    _expandedRules.Add(idx - 1);
                }
            }
        }

        _selectedIndices.Clear();
    }

    private void AddRuleFromParameterInternal(AnimatorControllerParameter parameter)
    {
        TransitionColorRule newRule = new TransitionColorRule
        {
            ParameterName = parameter.name,
            ParameterType = parameter.type,
            Color = GetRandomColor()
        };

        switch (parameter.type)
        {
            case AnimatorControllerParameterType.Bool:
                newRule.ConditionMode = AnimatorConditionMode.If;
                newRule.ThresholdValue = 1f;
                break;

            case AnimatorControllerParameterType.Trigger:
                newRule.ConditionMode = AnimatorConditionMode.If;
                break;

            case AnimatorControllerParameterType.Int:
            case AnimatorControllerParameterType.Float:
                newRule.ConditionMode = AnimatorConditionMode.Equals;
                newRule.ThresholdValue = parameter.defaultFloat;
                break;
        }

        _colorRules.Add(newRule);

        _selectedIndices.Clear();
        _selectedIndices.Add(_colorRules.Count - 1);
        _expandedRules.Add(_colorRules.Count - 1);
    }

    private void HandleRuleSelection(int index, bool multiSelect)
    {
        if (!multiSelect)
        {
            // Single selection mode
            _selectedIndices.Clear();
            _selectedIndices.Add(index);
            _lastSelectedIndex = index;
        }
        else if (Event.current.shift && _lastSelectedIndex >= 0)
        {
            // Shift selection - select range between last selected and current
            _selectedIndices.Clear();

            // Determine range bounds
            int startIdx = Mathf.Min(_lastSelectedIndex, index);
            int endIdx = Mathf.Max(_lastSelectedIndex, index);

            // Select all items in range
            for (int i = startIdx; i <= endIdx; i++)
            {
                _selectedIndices.Add(i);
            }
        }
        else if (Event.current.control)
        {
            // Control selection - toggle selection state
            if (_selectedIndices.Contains(index))
            {
                _selectedIndices.Remove(index);

                // If we're deselecting the last selected item, update _lastSelectedIndex
                if (_lastSelectedIndex == index)
                {
                    _lastSelectedIndex = _selectedIndices.Count > 0 ? _selectedIndices[_selectedIndices.Count - 1] : -1;
                }
            }
            else
            {
                _selectedIndices.Add(index);
                _lastSelectedIndex = index;
            }

            // Keep indices sorted for easier range operations
            _selectedIndices.Sort();
        }

        Repaint();
    }

    private void ApplyChanges(string operationName)
    {
        // Just apply the changes to the patch - no Undo.RecordObject calls here
        AnimatorTransitionColorPatch.SetColorRules(_colorRules);

        // Refresh the UI
        Repaint();
    }

    private void RecordUndoAndApplyChanges(string operationName, System.Action operation)
    {
        // Record the window state for undo BEFORE making changes
        Undo.RecordObject(this, operationName);

        // Perform the operation
        operation();

        // Mark the object as dirty so Unity knows it changed
        EditorUtility.SetDirty(this);

        // Apply changes to the patch
        AnimatorTransitionColorPatch.SetColorRules(_colorRules);

        // Refresh the UI
        Repaint();
    }

    private Color GetRandomColor()
    {
        // Generate a random bright color
        return new Color(
            UnityEngine.Random.Range(0.5f, 1f),
            UnityEngine.Random.Range(0.5f, 1f),
            UnityEngine.Random.Range(0.5f, 1f)
        );
    }

    private HashSet<int> FindDuplicateRules()
    {
        HashSet<int> duplicateIndices = new HashSet<int>();
        Dictionary<string, List<int>> ruleKeys = new Dictionary<string, List<int>>();

        for (int i = 0; i < _colorRules.Count; i++)
        {
            TransitionColorRule rule = _colorRules[i];
            string key = $"{rule.ParameterName.ToLower()}|{rule.ParameterType}|{rule.ConditionMode}|{rule.ThresholdValue}";

            if (!ruleKeys.ContainsKey(key))
            {
                ruleKeys[key] = new List<int>();
            }

            ruleKeys[key].Add(i);
        }

        foreach (var kvp in ruleKeys)
        {
            if (kvp.Value.Count > 1)
            {
                foreach (int index in kvp.Value)
                {
                    duplicateIndices.Add(index);
                }
            }
        }

        return duplicateIndices;
    }

    private string GetCompactRuleDisplay(TransitionColorRule rule)
    {
        string conditionSymbol = GetConditionSymbol(rule.ConditionMode);
        string valueStr = "";

        // Format the value part based on parameter type
        if (rule.ParameterType == AnimatorControllerParameterType.Bool)
        {
            valueStr = rule.ThresholdValue >= 0.5f ? "true" : "false";
        }
        else if (rule.ParameterType == AnimatorControllerParameterType.Int)
        {
            valueStr = ((int)rule.ThresholdValue).ToString();
        }
        else if (rule.ParameterType != AnimatorControllerParameterType.Trigger)
        {
            valueStr = rule.ThresholdValue.ToString("0.##");
        }

        // Assemble the display string
        string valueDisplay = rule.ParameterType == AnimatorControllerParameterType.Trigger
            ? ""
            : $" {conditionSymbol} {valueStr}";

        return $"{rule.ParameterName} ({rule.ParameterType}){valueDisplay}";
    }

    private string GetConditionSymbol(AnimatorConditionMode mode)
    {
        switch (mode)
        {
            case AnimatorConditionMode.Equals:
                return "==";
            case AnimatorConditionMode.NotEqual:
                return "!=";
            case AnimatorConditionMode.Greater:
                return ">";
            case AnimatorConditionMode.Less:
                return "<";
            case AnimatorConditionMode.If:
                return "If";
            case AnimatorConditionMode.IfNot:
                return "IfNot";
            default:
                return mode.ToString();
        }
    }

    /// <summary>
    /// Get the current Animator Controller
    /// </summary>
    private AnimatorController GetAnimatorController()
    {
        // First try selection
        if (Selection.activeObject is AnimatorController controller)
        {
            return controller;
        }

        // Try Animator windows
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
                            return value as AnimatorController;
                        }
                        catch { }
                    }
                }
            }
        }

        return null;
    }

    private void SaveUISettings()
    {
        // Save animation settings to EditorPrefs
        EditorPrefs.SetBool("SDKatHome.AnimatorTransition.EnableFlash", _enableFlashAnimation);
        EditorPrefs.SetFloat("SDKatHome.AnimatorTransition.FlashSpeed", _flashSpeed);
        EditorPrefs.SetString("SDKatHome.AnimatorTransition.IncomingFlashColor", ColorUtility.ToHtmlStringRGBA(_incomingFlashColor));
        EditorPrefs.SetString("SDKatHome.AnimatorTransition.OutgoingFlashColor", ColorUtility.ToHtmlStringRGBA(_outgoingFlashColor));
        EditorPrefs.SetFloat("SDKatHome.AnimatorTransition.FlashIntensity", _flashIntensity);
        EditorPrefs.SetFloat("SDKatHome.AnimatorTransition.SequenceDelay", _sequenceDelay);
        EditorPrefs.SetBool("SDKatHome.AnimatorTransition.EnableSequential", _enableSequentialFlash);
    }

    private void LoadUISettings()
    {
        // Load animation settings from EditorPrefs
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

    private void ResetAllSettings()
    {
        RecordUndoAndApplyChanges("Reset All Settings", () =>
        {
            // Reset animation settings
            _enableFlashAnimation = true;
            _flashSpeed = 2.0f;
            _incomingFlashColor = Color.green;
            _outgoingFlashColor = Color.red;
            _flashIntensity = 0.8f;
            _sequenceDelay = 1.0f;
            _enableSequentialFlash = true;

            // Save animation settings to the patch
            AnimatorTransitionColorPatch.SetAnimationSettings(
                _enableFlashAnimation, _flashSpeed, _incomingFlashColor, _outgoingFlashColor,
                _flashIntensity, _sequenceDelay, _enableSequentialFlash);

            // Clear all rules
            _colorRules.Clear();
            AnimatorTransitionColorPatch.SetColorRules(_colorRules);
        });
    }

    private void ExportSettings()
    {
        string path = EditorUtility.SaveFilePanel("Export Settings", "", "AnimatorTransitionSettings.json", "json");
        if (!string.IsNullOrEmpty(path))
        {
            try
            {
                var settings = new SettingsData
                {
                    enableFlashAnimation = _enableFlashAnimation,
                    flashSpeed = _flashSpeed,
                    incomingFlashColor = ColorUtility.ToHtmlStringRGBA(_incomingFlashColor),
                    outgoingFlashColor = ColorUtility.ToHtmlStringRGBA(_outgoingFlashColor),
                    flashIntensity = _flashIntensity,
                    sequenceDelay = _sequenceDelay,
                    enableSequentialFlash = _enableSequentialFlash,
                    colorRules = _colorRules
                };

                string json = JsonUtility.ToJson(settings, true);
                System.IO.File.WriteAllText(path, json);

                EditorUtility.DisplayDialog("Export Complete", $"Settings exported to {path}", "OK");
            }
            catch (System.Exception ex)
            {
                EditorUtility.DisplayDialog("Export Failed", $"Failed to export settings: {ex.Message}", "OK");
            }
        }
    }

    private void ImportSettings()
    {
        string path = EditorUtility.OpenFilePanel("Import Settings", "", "json");
        if (!string.IsNullOrEmpty(path))
        {
            try
            {
                string json = System.IO.File.ReadAllText(path);
                var settings = JsonUtility.FromJson<SettingsData>(json);

                _enableFlashAnimation = settings.enableFlashAnimation;
                _flashSpeed = settings.flashSpeed;
                ColorUtility.TryParseHtmlString("#" + settings.incomingFlashColor, out _incomingFlashColor);
                ColorUtility.TryParseHtmlString("#" + settings.outgoingFlashColor, out _outgoingFlashColor);
                _flashIntensity = settings.flashIntensity;
                _sequenceDelay = settings.sequenceDelay;
                _enableSequentialFlash = settings.enableSequentialFlash;

                if (settings.colorRules != null)
                {
                    _colorRules = settings.colorRules;
                    foreach (var rule in _colorRules)
                    {
                        rule.UpdateColorFromHex();
                    }
                }

                // Save all settings
                AnimatorTransitionColorPatch.SetAnimationSettings(
                    _enableFlashAnimation,
                    _flashSpeed,
                    _incomingFlashColor,
                    _outgoingFlashColor,
                    _flashIntensity,
                    _sequenceDelay,
                    _enableSequentialFlash);
                AnimatorTransitionColorPatch.SetColorRules(_colorRules);
                SaveUISettings();

                EditorUtility.DisplayDialog("Import Complete", $"Settings imported from {path}", "OK");
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Import Failed", $"Failed to import settings: {ex.Message}", "OK");
            }
        }
    }

    // Settings data class for export/import:
    [Serializable]
    public class SettingsData
    {
        public bool enableFlashAnimation;
        public float flashSpeed;
        public string incomingFlashColor;
        public string outgoingFlashColor;
        public float flashIntensity;
        public float sequenceDelay;
        public bool enableSequentialFlash;
        public List<TransitionColorRule> colorRules;
    }

    /// <summary>
    /// Get available condition modes based on parameter type
    /// </summary>
    private AnimatorConditionMode[] GetAvailableConditionModes(AnimatorControllerParameterType parameterType)
    {
        switch (parameterType)
        {
            case AnimatorControllerParameterType.Bool:
                return new[] { AnimatorConditionMode.If, AnimatorConditionMode.IfNot };

            case AnimatorControllerParameterType.Trigger:
                return new[] { AnimatorConditionMode.If };

            case AnimatorControllerParameterType.Int:
                return new[] { AnimatorConditionMode.Equals, AnimatorConditionMode.NotEqual,
                               AnimatorConditionMode.Greater, AnimatorConditionMode.Less };

            case AnimatorControllerParameterType.Float:
                return new[] { AnimatorConditionMode.Greater, AnimatorConditionMode.Less };

            default:
                return new[] { AnimatorConditionMode.Equals };
        }
    }
}
#endif