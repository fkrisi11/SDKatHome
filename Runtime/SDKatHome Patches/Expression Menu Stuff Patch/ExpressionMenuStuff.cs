#if UNITY_EDITOR
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace SDKatHome.Patches
{
    [SDKPatch("Expression Menu Additions",
              "Adds a lot of new features to the Expression Menu editor",
              "UI Improvements",
              usePrefix: false,
              usePostfix: true)]
    public static class ExpressionMenuStuff
    {
        private static VRCExpressionsMenu.Control copiedControl = null;

        private static Button copyButton;
        private static Button pasteButton;
        private static Button duplicateButton;
        private static Button backButton;
        private static Button forwardButton;
        private static ListView currentListView;
        private static VRCExpressionsMenuEditor currentEditor;
        private static ObjectField currentMenuField;

        // Rich text editor elements
        private static Foldout richTextFoldout;
        private static VisualElement richTextContainer;
        private static TextField nameField;
        private static Button boldButton;
        private static Button italicButton;
        private static Button underlineButton;
        private static Button strikethroughButton;
        private static ColorField colorPicker;
        private static SliderInt sizeSlider;
        private static Button lineBreakButton;
        private static Button clearFormattingButton;
        private static Button applyColorButton;
        private static Button applySizeButton;
        private static Button makeUniqueButton;
        private static Label previewLabel;
        private static bool isUpdatingFromCode = false;

        // Navigation history
        private static List<VRCExpressionsMenu> navigationHistory = new List<VRCExpressionsMenu>();
        private static int currentHistoryIndex = -1;
        private static bool isNavigatingProgrammatically = false;

        // Remember foldout state
        private static bool rememberedFoldoutState = false;

        // Target the CreateInspectorGUI method to add our buttons
        public static System.Reflection.MethodBase TargetMethod()
        {
            return typeof(VRCExpressionsMenuEditor).GetMethod("CreateInspectorGUI");
        }

        private static bool isMonitoringChanges = false;
        private static string lastKnownControlName = "";

        private static int lastCursorPos = 0;
        private static int lastSelectPos = 0;

        private static int capturedSelectionStart = -1;
        private static int capturedSelectionEnd = -1;
        private static bool hasValidCapturedSelection = false;

        private static Button clearColorButton;
        private static Button clearSizeButton;

        [HarmonyPostfix]
        public static void Postfix(VRCExpressionsMenuEditor __instance, ref VisualElement __result)
        {
            try
            {
                currentEditor = __instance;

                // Add current menu to navigation history if it's a new menu and not navigating programmatically
                if (currentEditor.Menu != null && !isNavigatingProgrammatically)
                {
                    AddToNavigationHistory(currentEditor.Menu);
                }

                // Find the ListView for controls
                var controlsListView = __result.Q<ListView>("ControlsListView");
                if (controlsListView != null)
                {
                    currentListView = controlsListView;

                    // Add keyboard event handling to the root element
                    __result.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);

                    // Add double-click handling - simpler approach
                    controlsListView.RegisterCallback<MouseDownEvent>(OnListViewMouseDown);

                    // Make sure the root can receive focus for keyboard events
                    __result.focusable = true;
                    __result.tabIndex = 0;

                    // Create navigation button container
                    var navContainer = new VisualElement();
                    navContainer.style.flexDirection = FlexDirection.Row;
                    navContainer.style.marginBottom = 5;
                    navContainer.style.marginTop = 5;

                    var infoContainer = new VisualElement();
                    infoContainer.style.flexDirection = FlexDirection.Row;
                    infoContainer.style.marginBottom = 5;
                    infoContainer.style.alignItems = Align.Center;

                    var currentMenuLabel = new Label("Menu");
                    currentMenuLabel.style.marginRight = 5;
                    currentMenuLabel.style.marginLeft = 3;
                    currentMenuLabel.style.minWidth = 114;

                    currentMenuField = new ObjectField();
                    currentMenuField.objectType = typeof(VRCExpressionsMenu);
                    currentMenuField.style.flexGrow = 1;
                    currentMenuField.SetEnabled(false); // Make it readonly - shows the asset but can't be changed
                    currentMenuField.value = currentEditor.Menu;

                    infoContainer.Add(currentMenuLabel);
                    infoContainer.Add(currentMenuField);

                    // Create navigation buttons
                    backButton = new Button(() => NavigateBack())
                    {
                        text = "◀ Back",
                        style = { minWidth = 60, marginRight = 5 }
                    };

                    forwardButton = new Button(() => NavigateForward())
                    {
                        text = "Forward ▶",
                        style = { minWidth = 70, marginRight = 10 }
                    };

                    navContainer.Add(backButton);
                    navContainer.Add(forwardButton);

                    // Create main button container
                    var buttonContainer = new VisualElement();
                    buttonContainer.style.flexDirection = FlexDirection.Row;
                    buttonContainer.style.marginBottom = 5;

                    // Create buttons with hotkey indicators
                    copyButton = new Button(() => CopySelectedControl())
                    {
                        text = "Copy (Ctrl+C)",
                        style = { minWidth = 90, marginRight = 5 }
                    };

                    pasteButton = new Button(() => ShowPasteOptions())
                    {
                        text = "Paste ▼ (Ctrl+V)",
                        style = { minWidth = 110, marginRight = 5 }
                    };

                    duplicateButton = new Button(() => DuplicateSelectedControl())
                    {
                        text = "Duplicate (Ctrl+D)",
                        style = { minWidth = 130, marginRight = 5 }
                    };

                    makeUniqueButton = new Button(() => ShowMakeUniqueConfirmation())
                    {
                        text = "Make Unique",
                        style = { minWidth = 80, marginRight = 5 },
                    };

                    // Add buttons to container
                    buttonContainer.Add(copyButton);
                    buttonContainer.Add(pasteButton);
                    buttonContainer.Add(duplicateButton);

                    // Create second button container for Make Unique
                    var utilityContainer = new VisualElement();
                    utilityContainer.style.flexDirection = FlexDirection.Row;
                    utilityContainer.style.marginBottom = 5;

                    makeUniqueButton = new Button(() => ShowMakeUniqueConfirmation())
                    {
                        text = "Make Unique",
                        style = { minWidth = 80, marginRight = 5 }
                    };

                    utilityContainer.Add(makeUniqueButton);

                    // Create rich text editor
                    CreateRichTextEditor();

                    // Insert button containers before the ListView
                    var listViewParent = controlsListView.parent;
                    listViewParent.Insert(listViewParent.IndexOf(controlsListView), infoContainer);
                    listViewParent.Insert(listViewParent.IndexOf(controlsListView), navContainer);
                    listViewParent.Insert(listViewParent.IndexOf(controlsListView), buttonContainer);
                    listViewParent.Insert(listViewParent.IndexOf(controlsListView), utilityContainer);
                    listViewParent.Insert(listViewParent.IndexOf(controlsListView), richTextFoldout);

                    // Update button states initially
                    UpdateButtonStates();

                    // Listen for selection changes to update button states and rich text editor
                    controlsListView.selectionChanged += _ =>
                    {
                        UpdateButtonStates();
                        UpdateRichTextEditor();
                        UpdateLastKnownControlName();
                    };

                    // Start monitoring for external changes
                    StartChangeMonitoring();
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Failed to add copy/paste functionality: {e}");
            }
        }

        private static bool FindContainingTag(string fullText, int selectionStart, int selectionEnd, string tagName, out int tagStart, out int tagEnd, out string beforeSelection, out string selectedText, out string afterSelection)
        {
            tagStart = -1;
            tagEnd = -1;
            beforeSelection = "";
            selectedText = "";
            afterSelection = "";

            try
            {
                // Create pattern for the specific tag (handles both simple tags and tags with attributes)
                string pattern;
                if (tagName == "color")
                {
                    pattern = @"<color=([^>]*?)>(.*?)</color>";
                }
                else if (tagName == "size")
                {
                    pattern = @"<size=([^>]*?)>(.*?)</size>";
                }
                else
                {
                    pattern = $@"<{tagName}(\s[^>]*)?>(.*?)</{tagName}>";
                }

                var matches = Regex.Matches(fullText, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

                foreach (Match match in matches)
                {
                    int matchStart = match.Index;
                    int matchEnd = match.Index + match.Length;

                    // Find the content group (different index based on tag type)
                    Group contentGroup;
                    if (tagName == "color" || tagName == "size")
                    {
                        contentGroup = match.Groups[2]; // Content is in group 2 for attribute tags
                    }
                    else
                    {
                        contentGroup = match.Groups[match.Groups.Count - 1]; // Last group for simple tags
                    }

                    int contentStart = contentGroup.Index;
                    int contentEnd = contentGroup.Index + contentGroup.Length;

                    // Check if the selection is entirely within this tag's content
                    if (selectionStart >= contentStart && selectionEnd <= contentEnd)
                    {
                        tagStart = matchStart;
                        tagEnd = matchEnd;

                        var fullContent = contentGroup.Value;
                        var selectionStartInContent = selectionStart - contentStart;
                        var selectionLengthInContent = selectionEnd - selectionStart;

                        beforeSelection = fullContent.Substring(0, selectionStartInContent);
                        selectedText = fullContent.Substring(selectionStartInContent, selectionLengthInContent);
                        afterSelection = fullContent.Substring(selectionStartInContent + selectionLengthInContent);

                        return true;
                    }
                }

                return false;
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error finding containing {tagName} tag: {e}");
                return false;
            }
        }

        private static string ExtractTagAttribute(string fullText, int tagStart, int tagEnd, string tagName, string attributeName = "")
        {
            try
            {
                var tagText = fullText.Substring(tagStart, tagEnd - tagStart);

                if (string.IsNullOrEmpty(attributeName))
                {
                    // For simple tags, return empty string
                    return "";
                }

                // For attribute tags like color=#FF0000 or size=18
                var pattern = $@"<{tagName}=([^>]*?)>";
                var match = Regex.Match(tagText, pattern, RegexOptions.IgnoreCase);

                if (match.Success)
                {
                    return match.Groups[1].Value;
                }

                return "";
            }
            catch
            {
                return "";
            }
        }

        private static void CreateRichTextEditor()
        {
            // Create collapsible foldout
            richTextFoldout = new Foldout();
            richTextFoldout.text = "Rich Text Editor";
            richTextFoldout.style.marginBottom = 10;
            richTextFoldout.style.marginTop = 5;
            richTextFoldout.value = rememberedFoldoutState;

            // Remember foldout state when it changes
            richTextFoldout.RegisterValueChangedCallback(evt => rememberedFoldoutState = evt.newValue);

            richTextContainer = new VisualElement();
            richTextContainer.style.marginTop = 5;
            richTextContainer.style.backgroundColor = new Color(0.2f, 0.2f, 0.2f, 0.3f);
            richTextContainer.style.paddingTop = 5;
            richTextContainer.style.paddingBottom = 5;
            richTextContainer.style.paddingLeft = 5;
            richTextContainer.style.paddingRight = 5;
            richTextContainer.style.borderTopWidth = 1;
            richTextContainer.style.borderBottomWidth = 1;
            richTextContainer.style.borderLeftWidth = 1;
            richTextContainer.style.borderRightWidth = 1;
            richTextContainer.style.borderTopColor = Color.gray;
            richTextContainer.style.borderBottomColor = Color.gray;
            richTextContainer.style.borderLeftColor = Color.gray;
            richTextContainer.style.borderRightColor = Color.gray;

            // Name field with proper undo support
            nameField = new TextField("Name:");
            nameField.style.marginBottom = 5;
            nameField.RegisterValueChangedCallback(OnNameFieldChanged);

            nameField.RegisterCallback<FocusInEvent>(evt =>
            {
                EditorApplication.delayCall += TrackSelection;
            });

            nameField.RegisterCallback<FocusOutEvent>(evt =>
            {
                lastCursorPos = 0;
                lastSelectPos = 0;
            });

            nameField.RegisterCallback<MouseUpEvent>(evt =>
            {
                EditorApplication.delayCall += TrackSelection;
            });

            nameField.RegisterCallback<KeyUpEvent>(evt =>
            {
                EditorApplication.delayCall += TrackSelection;
            });

            richTextContainer.Add(nameField);

            // Formatting buttons row 1
            var formatRow1 = new VisualElement();
            formatRow1.style.flexDirection = FlexDirection.Row;
            formatRow1.style.marginBottom = 3;
            formatRow1.style.flexWrap = Wrap.Wrap;

            boldButton = new Button(() => ToggleFormatting("b"))
            {
                text = "B",
                style = {
                    minWidth = 30,
                    marginRight = 3,
                    marginBottom = 3,
                    unityFontStyleAndWeight = FontStyle.Bold
                }
            };
            // Capture selection before button steals focus
            boldButton.RegisterCallback<MouseDownEvent>(evt => CaptureCurrentSelection(), TrickleDown.TrickleDown);

            italicButton = new Button(() => ToggleFormatting("i"))
            {
                text = "I",
                style = {
                    minWidth = 30,
                    marginRight = 3,
                    marginBottom = 3,
                    unityFontStyleAndWeight = FontStyle.Italic
                }
            };
            italicButton.RegisterCallback<MouseDownEvent>(evt => CaptureCurrentSelection(), TrickleDown.TrickleDown);

            underlineButton = new Button(() => ToggleFormatting("u"))
            {
                text = "_U_",
                style = { minWidth = 30, marginRight = 3, marginBottom = 3 }
            };
            underlineButton.RegisterCallback<MouseDownEvent>(evt => CaptureCurrentSelection(), TrickleDown.TrickleDown);

            strikethroughButton = new Button(() => ToggleFormatting("s"))
            {
                text = "~~S~~",
                style = { minWidth = 40, marginRight = 3, marginBottom = 3 }
            };
            strikethroughButton.RegisterCallback<MouseDownEvent>(evt => CaptureCurrentSelection(), TrickleDown.TrickleDown);

            lineBreakButton = new Button(() => InsertText("\n"))
            {
                text = "↵",
                style = { minWidth = 30, marginRight = 3, marginBottom = 3 }
            };
            lineBreakButton.RegisterCallback<MouseDownEvent>(evt => CaptureCurrentSelection(), TrickleDown.TrickleDown);

            clearFormattingButton = new Button(() => ClearFormatting())
            {
                text = "Clear",
                style = { minWidth = 40, marginRight = 3, marginBottom = 3 }
            };
            clearFormattingButton.RegisterCallback<MouseDownEvent>(evt => CaptureCurrentSelection(), TrickleDown.TrickleDown);

            formatRow1.Add(boldButton);
            formatRow1.Add(italicButton);
            formatRow1.Add(underlineButton);
            formatRow1.Add(strikethroughButton);
            formatRow1.Add(lineBreakButton);
            formatRow1.Add(clearFormattingButton);
            richTextContainer.Add(formatRow1);

            // Formatting controls row 2
            var formatRow2 = new VisualElement();
            formatRow2.style.flexDirection = FlexDirection.Row;
            formatRow2.style.marginBottom = 5;
            formatRow2.style.alignItems = Align.Center;
            formatRow2.style.flexWrap = Wrap.Wrap;

            var colorLabel = new Label("Color:");
            colorLabel.style.marginRight = 5;
            colorLabel.style.minWidth = 35;

            colorPicker = new ColorField();
            colorPicker.style.marginRight = 5;
            colorPicker.style.marginBottom = 3;
            colorPicker.style.width = 50;
            colorPicker.value = Color.white;
            colorPicker.RegisterValueChangedCallback(OnColorChanged);

            applyColorButton = new Button(() => ApplyColor())
            {
                text = "Apply Color",
                style = { marginRight = 5, marginBottom = 3, minWidth = 70 }
            };
            applyColorButton.RegisterCallback<MouseDownEvent>(evt => CaptureCurrentSelection(), TrickleDown.TrickleDown);

            clearColorButton = new Button(() => ClearColors())
            {
                text = "Clear Colors",
                style = { marginRight = 10, marginBottom = 3, minWidth = 75 }
            };
            clearColorButton.RegisterCallback<MouseDownEvent>(evt => CaptureCurrentSelection(), TrickleDown.TrickleDown);

            var sizeLabel = new Label("Size:");
            sizeLabel.style.marginRight = 5;
            sizeLabel.style.minWidth = 30;

            sizeSlider = new SliderInt(8, 72);
            sizeSlider.style.width = 80;
            sizeSlider.style.marginRight = 5;
            sizeSlider.style.marginBottom = 3;
            sizeSlider.value = 18;
            sizeSlider.RegisterValueChangedCallback(OnSizeChanged);

            var sizeValueLabel = new Label("18");
            sizeValueLabel.style.minWidth = 20;
            sizeValueLabel.style.marginRight = 5;
            sizeValueLabel.style.marginBottom = 3;
            sizeSlider.RegisterValueChangedCallback(evt => sizeValueLabel.text = evt.newValue.ToString());

            applySizeButton = new Button(() => ApplySize())
            {
                text = "Apply Size",
                style = { marginBottom = 3, minWidth = 65 }
            };
            applySizeButton.RegisterCallback<MouseDownEvent>(evt => CaptureCurrentSelection(), TrickleDown.TrickleDown);

            clearSizeButton = new Button(() => ClearSizes())
            {
                text = "Clear Sizes",
                style = { marginLeft = 5, marginBottom = 3, minWidth = 70 }
            };
            clearSizeButton.RegisterCallback<MouseDownEvent>(evt => CaptureCurrentSelection(), TrickleDown.TrickleDown);

            formatRow2.Add(colorLabel);
            formatRow2.Add(colorPicker);
            formatRow2.Add(applyColorButton);
            formatRow2.Add(clearColorButton);
            formatRow2.Add(sizeLabel);
            formatRow2.Add(sizeSlider);
            formatRow2.Add(sizeValueLabel);
            formatRow2.Add(applySizeButton);
            formatRow2.Add(clearSizeButton);
            richTextContainer.Add(formatRow2);

            // Preview
            var previewContainer = new VisualElement();
            previewContainer.style.marginTop = 5;
            previewContainer.style.backgroundColor = new Color(0.1f, 0.1f, 0.1f, 0.5f);
            previewContainer.style.paddingTop = 5;
            previewContainer.style.paddingBottom = 5;
            previewContainer.style.paddingLeft = 5;
            previewContainer.style.paddingRight = 5;

            var previewTitle = new Label("Preview:");
            previewTitle.style.fontSize = 10;
            previewTitle.style.marginBottom = 3;

            previewLabel = new Label("Sample Text");
            previewLabel.style.fontSize = 14;
            previewLabel.style.whiteSpace = WhiteSpace.Normal;

            previewContainer.Add(previewTitle);
            previewContainer.Add(previewLabel);
            richTextContainer.Add(previewContainer);

            // Add container to foldout
            richTextFoldout.Add(richTextContainer);
        }

        private static bool FindContainingColorTag(string fullText, int selectionStart, int selectionEnd, out int colorTagStart, out int colorTagEnd, out string existingColor, out string beforeSelection, out string selectedText, out string afterSelection)
        {
            colorTagStart = -1;
            colorTagEnd = -1;
            existingColor = "";
            beforeSelection = "";
            selectedText = "";
            afterSelection = "";

            try
            {
                // Find all color tags in the text
                var colorPattern = @"<color=([^>]*?)>(.*?)</color>";
                var matches = Regex.Matches(fullText, colorPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

                foreach (Match match in matches)
                {
                    int tagStart = match.Index;
                    int tagEnd = match.Index + match.Length;
                    int contentStart = match.Groups[2].Index;
                    int contentEnd = match.Groups[2].Index + match.Groups[2].Length;

                    // Check if the selection is entirely within this color tag's content
                    if (selectionStart >= contentStart && selectionEnd <= contentEnd)
                    {
                        colorTagStart = tagStart;
                        colorTagEnd = tagEnd;
                        existingColor = match.Groups[1].Value;

                        var fullContent = match.Groups[2].Value;
                        var selectionStartInContent = selectionStart - contentStart;
                        var selectionLengthInContent = selectionEnd - selectionStart;

                        beforeSelection = fullContent.Substring(0, selectionStartInContent);
                        selectedText = fullContent.Substring(selectionStartInContent, selectionLengthInContent);
                        afterSelection = fullContent.Substring(selectionStartInContent + selectionLengthInContent);

                        return true;
                    }
                }

                return false;
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error finding containing color tag: {e}");
                return false;
            }
        }

        private static void CaptureCurrentSelection()
        {
            try
            {
                if (nameField != null && nameField.focusController?.focusedElement == nameField)
                {
                    int cursor = nameField.cursorIndex;
                    int select = nameField.selectIndex;

                    int start = Math.Min(cursor, select);
                    int end = Math.Max(cursor, select);
                    bool hasSelection = start != end;

                    if (hasSelection)
                    {
                        capturedSelectionStart = start;
                        capturedSelectionEnd = end;
                        hasValidCapturedSelection = true;

                        if (nameField.value != null && end <= nameField.value.Length)
                        {
                            string selectedText = nameField.value.Substring(start, end - start);
                        }
                    }
                    else
                    {
                        hasValidCapturedSelection = false;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error capturing selection: {e}");
            }
        }

        private static void TrackSelection()
        {
            try
            {
                if (nameField != null)
                {
                    // Get current positions
                    int currentCursor = nameField.cursorIndex;
                    int currentSelect = nameField.selectIndex;

                    // Update tracked positions
                    lastCursorPos = currentCursor;
                    lastSelectPos = currentSelect;

                    // Capture selection for use when focus is lost
                    int start = Math.Min(currentCursor, currentSelect);
                    int end = Math.Max(currentCursor, currentSelect);
                    bool hasSelection = start != end;

                    if (hasSelection)
                    {
                        capturedSelectionStart = start;
                        capturedSelectionEnd = end;
                        hasValidCapturedSelection = true;

                        if (nameField.value != null && end <= nameField.value.Length)
                        {
                            string selectedText = nameField.value.Substring(start, end - start);
                        }
                    }
                    else
                    {
                        // Only clear captured selection if we're sure there's no selection
                        if (currentCursor == currentSelect)
                        {
                            hasValidCapturedSelection = false;
                        }
                    }

                    UpdateToggleButtonStates();
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error tracking selection: {e}");
            }
        }

        private static void UpdateRichTextEditor()
        {
            try
            {
                if (currentListView == null || currentEditor?.Menu == null)
                {
                    // Clear the name field but don't hide the editor
                    if (nameField != null)
                    {
                        isUpdatingFromCode = true;
                        nameField.SetValueWithoutNotify("");
                        isUpdatingFromCode = false;
                    }
                    UpdateToggleButtonStates();
                    UpdatePreview();
                    return;
                }

                int selectedIndex = currentListView.selectedIndex;
                if (selectedIndex >= 0 && selectedIndex < currentEditor.Menu.controls.Count)
                {
                    var selectedControl = currentEditor.Menu.controls[selectedIndex];

                    // Update name field without triggering the callback
                    isUpdatingFromCode = true;
                    nameField.SetValueWithoutNotify(selectedControl.name ?? "");
                    isUpdatingFromCode = false;

                    UpdateToggleButtonStates();
                    UpdatePreview();
                }
                else
                {
                    // Clear the name field but don't hide the editor
                    isUpdatingFromCode = true;
                    nameField.SetValueWithoutNotify("");
                    isUpdatingFromCode = false;

                    UpdateToggleButtonStates();
                    UpdatePreview();
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error updating rich text editor: {e}");
            }
        }

        private static void StartChangeMonitoring()
        {
            if (!isMonitoringChanges)
            {
                isMonitoringChanges = true;
                EditorApplication.update += MonitorControlChanges;
                UpdateLastKnownControlName();
            }
        }

        private static void StopChangeMonitoring()
        {
            if (isMonitoringChanges)
            {
                isMonitoringChanges = false;
                EditorApplication.update -= MonitorControlChanges;
            }
        }

        private static void OnEditorDestroyed()
        {
            StopChangeMonitoring();
            currentEditor = null;
            currentListView = null;
            nameField = null;
            lastKnownControlName = "";
        }

        private static void MonitorControlChanges()
        {
            try
            {
                // Only monitor if we have a valid setup
                if (currentEditor?.Menu == null || currentListView == null || nameField == null)
                {
                    return;
                }

                int selectedIndex = currentListView.selectedIndex;
                if (selectedIndex >= 0 && selectedIndex < currentEditor.Menu.controls.Count)
                {
                    var currentControlName = currentEditor.Menu.controls[selectedIndex].name ?? "";

                    // Check if the control name changed externally (not from our rich text editor)
                    if (currentControlName != lastKnownControlName && currentControlName != nameField.value)
                    {
                        // Update our rich text field to match the external change
                        isUpdatingFromCode = true;
                        nameField.SetValueWithoutNotify(currentControlName);
                        isUpdatingFromCode = false;

                        // Update our tracking variable
                        lastKnownControlName = currentControlName;

                        // Update the preview and button states
                        UpdateToggleButtonStates();
                        UpdatePreview();
                    }
                }
            }
            catch (Exception)
            {
                // Don't spam the console with errors from the update loop
                // Debug.LogError($"[SDK@Home] Error monitoring control changes: {e}");
            }
        }

        private static void UpdateLastKnownControlName()
        {
            try
            {
                if (currentEditor?.Menu == null || currentListView == null)
                {
                    lastKnownControlName = "";
                    return;
                }

                int selectedIndex = currentListView.selectedIndex;
                if (selectedIndex >= 0 && selectedIndex < currentEditor.Menu.controls.Count)
                {
                    lastKnownControlName = currentEditor.Menu.controls[selectedIndex].name ?? "";
                }
                else
                {
                    lastKnownControlName = "";
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error updating last known control name: {e}");
            }
        }

        private static void UpdateToggleButtonStates()
        {
            try
            {
                var text = nameField.value;
                int selectionStart, selectionEnd;
                bool hasSelection = GetSelectionRange(out selectionStart, out selectionEnd);

                string textToCheck;
                if (hasSelection && selectionEnd <= text.Length)
                {
                    textToCheck = text.Substring(selectionStart, selectionEnd - selectionStart);
                }
                else
                {
                    textToCheck = text;
                }

                // Update formatting button states
                UpdateToggleButton(boldButton, textToCheck, "b");
                UpdateToggleButton(italicButton, textToCheck, "i");
                UpdateToggleButton(underlineButton, textToCheck, "u");
                UpdateToggleButton(strikethroughButton, textToCheck, "s");

                // Update clear buttons enable state
                if (clearColorButton != null)
                {
                    clearColorButton.SetEnabled(IsSelectionWithinFormattingTag("color"));
                }

                if (clearSizeButton != null)
                {
                    clearSizeButton.SetEnabled(IsSelectionWithinFormattingTag("size"));
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error updating toggle button states: {e}");
            }
        }

        private static bool GetSelectionRange(out int selectionStart, out int selectionEnd)
        {
            selectionStart = 0;
            selectionEnd = 0;

            try
            {
                if (nameField == null) return false;

                // First, try to use captured selection if we have it
                if (hasValidCapturedSelection)
                {
                    selectionStart = capturedSelectionStart;
                    selectionEnd = capturedSelectionEnd;

                    // Validate the captured selection against current text
                    int textLength = nameField.value?.Length ?? 0;
                    if (selectionStart >= 0 && selectionEnd <= textLength && selectionStart < selectionEnd)
                    {
                        return true;
                    }
                    else
                    {
                        hasValidCapturedSelection = false;
                    }
                }

                // Fallback to current field selection
                int cursor = nameField.cursorIndex;
                int select = nameField.selectIndex;

                // Ensure we have valid positions
                int textLength2 = nameField.value?.Length ?? 0;
                cursor = Math.Max(0, Math.Min(cursor, textLength2));
                select = Math.Max(0, Math.Min(select, textLength2));

                selectionStart = Math.Min(cursor, select);
                selectionEnd = Math.Max(cursor, select);

                bool hasSelection = selectionStart != selectionEnd;

                return hasSelection;
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error getting selection range: {e}");
                return false;
            }
        }

        private static void UpdateToggleButton(Button button, string text, string tag)
        {
            bool hasTag = IsTextWrappedInTag(text, tag);

            if (hasTag)
            {
                button.style.backgroundColor = new Color(0.3f, 0.6f, 1f, 0.8f); // Highlight active buttons
            }
            else
            {
                button.style.backgroundColor = StyleKeyword.Initial; // Reset to default
            }
        }

        private static bool IsTextWrappedInTag(string text, string tag)
        {
            if (string.IsNullOrEmpty(text)) return false;

            // Remove whitespace for more accurate detection
            text = text.Trim();

            // Create a more precise regex that matches the tag at the very beginning and end
            var pattern = $@"^<{tag}(\s[^>]*)?>(.*)</{tag}>$";
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (match.Success)
            {
                // Make sure there are no other opening tags of the same type inside
                var innerContent = match.Groups[2].Value;
                var innerTagPattern = $@"<{tag}(\s[^>]*)?>";
                return !Regex.IsMatch(innerContent, innerTagPattern, RegexOptions.IgnoreCase);
            }

            return false;
        }

        private static void OnNameFieldChanged(ChangeEvent<string> evt)
        {
            try
            {
                if (isUpdatingFromCode) return;

                if (currentListView == null || currentEditor?.Menu == null) return;

                int selectedIndex = currentListView.selectedIndex;
                if (selectedIndex >= 0 && selectedIndex < currentEditor.Menu.controls.Count)
                {
                    // Record undo for the menu change
                    Undo.RecordObject(currentEditor.Menu, "Change Control Name");
                    currentEditor.Menu.controls[selectedIndex].name = evt.newValue;
                    EditorUtility.SetDirty(currentEditor.Menu);

                    // Update our tracking variable
                    lastKnownControlName = evt.newValue;

                    // Also refresh the ListView to show the updated name
                    currentListView.RefreshItems();

                    UpdatePreview();
                    UpdateToggleButtonStates();
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error changing control name: {e}");
            }
        }

        private static void ToggleFormatting(string tag)
        {
            try
            {
                var currentText = nameField.value ?? "";
                int selectionStart, selectionEnd;
                bool hasSelection = GetSelectionRange(out selectionStart, out selectionEnd);

                string newText;
                int newCursorPos;
                int newSelectPos;

                if (hasSelection && selectionStart >= 0 && selectionEnd <= currentText.Length && selectionStart < selectionEnd)
                {
                    var selectedText = currentText.Substring(selectionStart, selectionEnd - selectionStart);

                    // Check if selection is exactly wrapped in this tag
                    if (IsTextWrappedInTag(selectedText, tag))
                    {
                        var processedText = ToggleTagInText(selectedText, tag);
                        newText = currentText.Substring(0, selectionStart) +
                                 processedText +
                                 currentText.Substring(selectionEnd);
                        newCursorPos = selectionStart + processedText.Length;
                        newSelectPos = newCursorPos;
                    }
                    else
                    {
                        // Check if selection is within a larger tag
                        int tagStart, tagEnd;
                        string beforeSelection, afterSelection, selectedPortion;

                        if (FindContainingTag(currentText, selectionStart, selectionEnd, tag, out tagStart, out tagEnd, out beforeSelection, out selectedPortion, out afterSelection))
                        {

                            // Split the tag: remove tag from selected part, keep it on before/after parts
                            string replacement = "";

                            // Add the before part with tag (if not empty)
                            if (!string.IsNullOrEmpty(beforeSelection))
                            {
                                replacement += $"<{tag}>{beforeSelection}</{tag}>";
                            }

                            // Add the selected part without tag (removing it)
                            replacement += selectedPortion;

                            // Add the after part with tag (if not empty)
                            if (!string.IsNullOrEmpty(afterSelection))
                            {
                                replacement += $"<{tag}>{afterSelection}</{tag}>";
                            }

                            // Replace the entire original tag with the split version
                            newText = currentText.Substring(0, tagStart) +
                                     replacement +
                                     currentText.Substring(tagEnd);

                            // Calculate new cursor position
                            int replacementOffset = 0;
                            if (!string.IsNullOrEmpty(beforeSelection))
                            {
                                replacementOffset += $"<{tag}>{beforeSelection}</{tag}>".Length;
                            }
                            replacementOffset += selectedPortion.Length;

                            newCursorPos = tagStart + replacementOffset;
                            newSelectPos = newCursorPos;
                        }
                        else
                        {
                            // Add tag around the selected text
                            var processedText = $"<{tag}>{selectedText}</{tag}>";
                            newText = currentText.Substring(0, selectionStart) +
                                     processedText +
                                     currentText.Substring(selectionEnd);
                            newCursorPos = selectionStart + processedText.Length;
                            newSelectPos = newCursorPos;
                        }
                    }
                }
                else
                {
                    // Work with entire text using existing logic
                    newText = ToggleTagInEntireTextImproved(currentText, tag);
                    newCursorPos = newText.Length;
                    newSelectPos = newCursorPos;
                }

                // Clear captured selection after use
                hasValidCapturedSelection = false;

                // Record undo
                if (currentListView != null && currentEditor?.Menu != null)
                {
                    int selectedIndex = currentListView.selectedIndex;
                    if (selectedIndex >= 0 && selectedIndex < currentEditor.Menu.controls.Count)
                    {
                        Undo.RecordObject(currentEditor.Menu, $"Toggle {tag.ToUpper()} Formatting");
                    }
                }

                // Apply the changes
                nameField.value = newText;

                EditorApplication.delayCall += () =>
                {
                    if (nameField != null)
                    {
                        nameField.cursorIndex = newCursorPos;
                        nameField.selectIndex = newSelectPos;
                        lastCursorPos = newCursorPos;
                        lastSelectPos = newSelectPos;
                    }
                };

                lastKnownControlName = newText;

                UpdateToggleButtonStates();
                UpdatePreview();
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error toggling formatting: {e}");
            }
        }

        private static bool IsTextWrappedInColor(string text, out string innerContent, out string existingColor)
        {
            innerContent = text;
            existingColor = "";

            if (string.IsNullOrEmpty(text)) return false;

            // Check if the entire text is wrapped in a color tag
            var colorPattern = @"^<color=([^>]*)>(.*)</color>$";
            var match = Regex.Match(text.Trim(), colorPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (match.Success)
            {
                existingColor = match.Groups[1].Value;
                innerContent = match.Groups[2].Value;
                return true;
            }

            return false;
        }

        private static string ToggleTagInEntireTextImproved(string text, string tag)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // Check if the ENTIRE text is wrapped in this tag (ignoring whitespace)
            var trimmedText = text.Trim();
            var outerTagPattern = $@"^<{tag}(\s[^>]*)?>(.*)</{tag}>$";
            var outerMatch = Regex.Match(trimmedText, outerTagPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (outerMatch.Success)
            {
                // Remove the outer tag, but preserve any leading/trailing whitespace from original
                var innerContent = outerMatch.Groups[2].Value;
                var leadingWhitespace = text.Substring(0, text.Length - text.TrimStart().Length);
                var trailingWhitespace = text.Substring(text.TrimEnd().Length);
                return leadingWhitespace + innerContent + trailingWhitespace;
            }

            // Check if we have color tags wrapping the entire text
            var colorPattern = @"^(\s*)<color=[^>]*>(.*)</color>(\s*)$";
            var colorMatch = Regex.Match(text, colorPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (colorMatch.Success)
            {
                var leadingSpace = colorMatch.Groups[1].Value;
                var innerContent = colorMatch.Groups[2].Value;
                var trailingSpace = colorMatch.Groups[3].Value;

                // Check if the inner content is wrapped in our tag
                var innerTagPattern = $@"^<{tag}(\s[^>]*)?>(.*)</{tag}>$";
                var innerMatch = Regex.Match(innerContent, innerTagPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

                if (innerMatch.Success)
                {
                    // Remove the tag from inside the color wrapper
                    var untaggedInner = innerMatch.Groups[2].Value;
                    return leadingSpace + $"<color={GetColorFromMatch(colorMatch.Value)}>{untaggedInner}</color>" + trailingSpace;
                }
                else
                {
                    // Add the tag inside the color wrapper
                    var taggedInner = $"<{tag}>{innerContent}</{tag}>";
                    return leadingSpace + $"<color={GetColorFromMatch(colorMatch.Value)}>{taggedInner}</color>" + trailingSpace;
                }
            }
            else
            {
                // No color wrapper, and no existing tag - add the tag around everything
                return $"<{tag}>{text}</{tag}>";
            }
        }

        private static string GetColorFromMatch(string colorTag)
        {
            var colorValueMatch = Regex.Match(colorTag, @"<color=([^>]*)>", RegexOptions.IgnoreCase);
            return colorValueMatch.Success ? colorValueMatch.Groups[1].Value : "#FFFFFF";
        }

        private static string ToggleTagInText(string text, string tag)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // Check if the selected text is completely wrapped in the tag
            var pattern = $@"^<{tag}(\s[^>]*)?>(.*)</{tag}>$";
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (match.Success)
            {
                // Remove the tag
                return match.Groups[2].Value;
            }
            else
            {
                // Add the tag around the selected text
                return $"<{tag}>{text}</{tag}>";
            }
        }


        private static void InsertText(string text)
        {
            try
            {
                var currentText = nameField.value;
                var cursorPos = nameField.cursorIndex;
                var newText = currentText.Insert(cursorPos, text);

                // Record undo
                if (currentListView != null && currentEditor?.Menu != null)
                {
                    int selectedIndex = currentListView.selectedIndex;
                    if (selectedIndex >= 0 && selectedIndex < currentEditor.Menu.controls.Count)
                    {
                        Undo.RecordObject(currentEditor.Menu, "Insert Text");
                    }
                }

                nameField.value = newText;
                nameField.cursorIndex = cursorPos + text.Length;
                nameField.selectIndex = nameField.cursorIndex;
                UpdatePreview();
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error inserting text: {e}");
            }
        }

        private static void ApplyColor()
        {
            try
            {
                var color = colorPicker.value;
                var hexColor = ColorUtility.ToHtmlStringRGB(color);
                var currentText = nameField.value ?? "";
                int selectionStart, selectionEnd;
                bool hasSelection = GetSelectionRange(out selectionStart, out selectionEnd);

                string newText;
                int newCursorPos;
                int newSelectPos;

                // Record undo
                if (currentListView != null && currentEditor?.Menu != null)
                {
                    int selectedIndex = currentListView.selectedIndex;
                    if (selectedIndex >= 0 && selectedIndex < currentEditor.Menu.controls.Count)
                    {
                        Undo.RecordObject(currentEditor.Menu, "Apply Color");
                    }
                }

                if (hasSelection && selectionStart >= 0 && selectionEnd <= currentText.Length && selectionStart < selectionEnd)
                {
                    var selectedText = currentText.Substring(selectionStart, selectionEnd - selectionStart);

                    // Check if selection is exactly wrapped in a color tag
                    string innerContent;
                    string existingColor;

                    if (IsTextWrappedInColor(selectedText, out innerContent, out existingColor))
                    {
                        // Replace the existing color with the new color
                        var coloredText = $"<color=#{hexColor}>{innerContent}</color>";
                        newText = currentText.Substring(0, selectionStart) +
                                 coloredText +
                                 currentText.Substring(selectionEnd);
                        newCursorPos = selectionStart + coloredText.Length;
                        newSelectPos = newCursorPos;
                    }
                    else
                    {
                        // Check if selection is within a larger color tag
                        int colorTagStart, colorTagEnd;
                        string beforeSelection, afterSelection;
                        string containingColor;
                        string selectedPortion;

                        if (FindContainingColorTag(currentText, selectionStart, selectionEnd, out colorTagStart, out colorTagEnd, out containingColor, out beforeSelection, out selectedPortion, out afterSelection))
                        {

                            // Split the color tag: keep parts before/after in old color, selected part in new color
                            string replacement = "";

                            // Add the before part in original color (if not empty)
                            if (!string.IsNullOrEmpty(beforeSelection))
                            {
                                replacement += $"<color={containingColor}>{beforeSelection}</color>";
                            }

                            // Add the selected part in new color
                            replacement += $"<color=#{hexColor}>{selectedPortion}</color>";

                            // Add the after part in original color (if not empty)
                            if (!string.IsNullOrEmpty(afterSelection))
                            {
                                replacement += $"<color={containingColor}>{afterSelection}</color>";
                            }

                            // Replace the entire original color tag with the split version
                            newText = currentText.Substring(0, colorTagStart) +
                                     replacement +
                                     currentText.Substring(colorTagEnd);

                            // Calculate new cursor position
                            int replacementOffset = 0;
                            if (!string.IsNullOrEmpty(beforeSelection))
                            {
                                replacementOffset += $"<color={containingColor}>{beforeSelection}</color>".Length;
                            }
                            replacementOffset += $"<color=#{hexColor}>{selectedPortion}</color>".Length;

                            newCursorPos = colorTagStart + replacementOffset;
                            newSelectPos = newCursorPos;
                        }
                        else
                        {
                            // Add new color tag around the selected text
                            var coloredText = $"<color=#{hexColor}>{selectedText}</color>";
                            newText = currentText.Substring(0, selectionStart) +
                                     coloredText +
                                     currentText.Substring(selectionEnd);
                            newCursorPos = selectionStart + coloredText.Length;
                            newSelectPos = newCursorPos;
                        }
                    }
                }
                else
                {
                    // Apply color to entire text, removing any existing color tags first
                    var cleanText = RemoveColorTags(currentText);
                    newText = $"<color=#{hexColor}>{cleanText}</color>";
                    newCursorPos = newText.Length;
                    newSelectPos = newCursorPos;
                }

                // Clear captured selection after use
                hasValidCapturedSelection = false;

                nameField.value = newText;

                // Use EditorApplication.delayCall to set cursor position
                EditorApplication.delayCall += () =>
                {
                    if (nameField != null)
                    {
                        nameField.cursorIndex = newCursorPos;
                        nameField.selectIndex = newSelectPos;
                        lastCursorPos = newCursorPos;
                        lastSelectPos = newSelectPos;
                    }
                };

                // Update tracking
                lastKnownControlName = newText;

                UpdatePreview();
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error applying color: {e}");
            }
        }

        private static bool IsSelectionWithinFormattingTag(string tagName)
        {
            try
            {
                var currentText = nameField.value ?? "";
                int selectionStart, selectionEnd;
                bool hasSelection = GetSelectionRange(out selectionStart, out selectionEnd);

                if (hasSelection && selectionStart >= 0 && selectionEnd <= currentText.Length && selectionStart < selectionEnd)
                {
                    var selectedText = currentText.Substring(selectionStart, selectionEnd - selectionStart);

                    // Check if selection is exactly wrapped
                    if (IsTextWrappedInTag(selectedText, tagName))
                    {
                        return true;
                    }

                    // Check if selection is within a larger tag
                    int tagStart, tagEnd;
                    string beforeSelection, afterSelection, selectedPortion;
                    return FindContainingTag(currentText, selectionStart, selectionEnd, tagName, out tagStart, out tagEnd, out beforeSelection, out selectedPortion, out afterSelection);
                }
                else
                {
                    // Check if entire text has the tag
                    return IsTextWrappedInTag(currentText, tagName);
                }
            }
            catch
            {
                return false;
            }
        }

        private static void RemoveColorFromSelection()
        {
            try
            {
                var currentText = nameField.value ?? "";
                int selectionStart, selectionEnd;
                bool hasSelection = GetSelectionRange(out selectionStart, out selectionEnd);

                if (!hasSelection || selectionStart < 0 || selectionEnd > currentText.Length || selectionStart >= selectionEnd)
                {
                    // No valid selection, remove all colors from entire text
                    var cleanText = RemoveColorTags(currentText);
                    nameField.value = cleanText;
                    lastKnownControlName = cleanText;
                    return;
                }

                var selectedText = currentText.Substring(selectionStart, selectionEnd - selectionStart);

                // Check if selection is exactly wrapped in color
                string innerContent;
                string existingColor;

                if (IsTextWrappedInColor(selectedText, out innerContent, out existingColor))
                {

                    var newText = currentText.Substring(0, selectionStart) +
                                 innerContent +
                                 currentText.Substring(selectionEnd);

                    ApplyTextChange(newText, selectionStart + innerContent.Length, "Remove Color");
                }
                else
                {
                    // Check if selection is within a larger color tag
                    int colorTagStart, colorTagEnd;
                    string beforeSelection, afterSelection, containingColor, selectedPortion;

                    if (FindContainingColorTag(currentText, selectionStart, selectionEnd, out colorTagStart, out colorTagEnd, out containingColor, out beforeSelection, out selectedPortion, out afterSelection))
                    {

                        // Split the color tag: keep parts before/after in old color, selected part uncolored
                        string replacement = "";

                        // Add the before part in original color (if not empty)
                        if (!string.IsNullOrEmpty(beforeSelection))
                        {
                            replacement += $"<color={containingColor}>{beforeSelection}</color>";
                        }

                        // Add the selected part without color
                        replacement += selectedPortion;

                        // Add the after part in original color (if not empty)
                        if (!string.IsNullOrEmpty(afterSelection))
                        {
                            replacement += $"<color={containingColor}>{afterSelection}</color>";
                        }

                        // Replace the entire original color tag with the split version
                        var newText = currentText.Substring(0, colorTagStart) +
                                     replacement +
                                     currentText.Substring(colorTagEnd);

                        // Calculate new cursor position
                        int newCursorPos = colorTagStart;
                        if (!string.IsNullOrEmpty(beforeSelection))
                        {
                            newCursorPos += $"<color={containingColor}>{beforeSelection}</color>".Length;
                        }
                        newCursorPos += selectedPortion.Length;

                        ApplyTextChange(newText, newCursorPos, "Remove Color");
                    }
                }

                // Clear captured selection after use
                hasValidCapturedSelection = false;
                UpdatePreview();
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error removing color from selection: {e}");
            }
        }

        private static void ApplyTextChange(string newText, int newCursorPos, string undoDescription)
        {
            // Record undo
            if (currentListView != null && currentEditor?.Menu != null)
            {
                int selectedIndex = currentListView.selectedIndex;
                if (selectedIndex >= 0 && selectedIndex < currentEditor.Menu.controls.Count)
                {
                    Undo.RecordObject(currentEditor.Menu, undoDescription);
                }
            }

            nameField.value = newText;

            EditorApplication.delayCall += () =>
            {
                if (nameField != null)
                {
                    nameField.cursorIndex = newCursorPos;
                    nameField.selectIndex = newCursorPos;
                    lastCursorPos = newCursorPos;
                    lastSelectPos = newCursorPos;
                }
            };

            lastKnownControlName = newText;
        }

        private static string RemoveColorTags(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // Remove complete color tag pairs first
            var result = Regex.Replace(text, @"<color=[^>]*>(.*?)</color>", "$1", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // Clean up any remaining orphaned color tags
            result = Regex.Replace(result, @"</?color[^>]*>", "", RegexOptions.IgnoreCase);

            return result;
        }

        private static void ClearColors()
        {
            try
            {
                int selectionStart, selectionEnd;
                bool hasSelection = GetSelectionRange(out selectionStart, out selectionEnd);

                if (hasSelection)
                {
                    // Remove color from selected text only
                    RemoveColorFromSelection();
                }
                else
                {
                    // Remove all colors from entire text
                    var currentText = nameField.value;
                    var cleanText = RemoveColorTags(currentText);

                    // Record undo
                    if (currentListView != null && currentEditor?.Menu != null)
                    {
                        int selectedIndex = currentListView.selectedIndex;
                        if (selectedIndex >= 0 && selectedIndex < currentEditor.Menu.controls.Count)
                        {
                            Undo.RecordObject(currentEditor.Menu, "Clear Colors");
                        }
                    }

                    nameField.value = cleanText;
                    nameField.cursorIndex = cleanText.Length;
                    nameField.selectIndex = cleanText.Length;

                    lastKnownControlName = cleanText;
                }

                // Clear captured selection after use
                hasValidCapturedSelection = false;
                UpdatePreview();
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error clearing colors: {e}");
            }
        }

        private static void ApplySize()
        {
            try
            {
                var size = sizeSlider.value;
                var currentText = nameField.value ?? "";
                int selectionStart, selectionEnd;
                bool hasSelection = GetSelectionRange(out selectionStart, out selectionEnd);

                string newText;
                int newCursorPos;
                int newSelectPos;

                // Record undo
                if (currentListView != null && currentEditor?.Menu != null)
                {
                    int selectedIndex = currentListView.selectedIndex;
                    if (selectedIndex >= 0 && selectedIndex < currentEditor.Menu.controls.Count)
                    {
                        Undo.RecordObject(currentEditor.Menu, "Apply Size");
                    }
                }

                if (hasSelection && selectionStart >= 0 && selectionEnd <= currentText.Length && selectionStart < selectionEnd)
                {
                    var selectedText = currentText.Substring(selectionStart, selectionEnd - selectionStart);

                    // Check if selection is exactly wrapped in a size tag
                    var sizePattern = @"^<size=([^>]*)>(.*)</size>$";
                    var exactMatch = Regex.Match(selectedText, sizePattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

                    if (exactMatch.Success)
                    {
                        var innerContent = exactMatch.Groups[2].Value;
                        var sizedText = $"<size={size}>{innerContent}</size>";
                        newText = currentText.Substring(0, selectionStart) +
                                 sizedText +
                                 currentText.Substring(selectionEnd);
                        newCursorPos = selectionStart + sizedText.Length;
                        newSelectPos = newCursorPos;
                    }
                    else
                    {
                        // Check if selection is within a larger size tag
                        int tagStart, tagEnd;
                        string beforeSelection, afterSelection, selectedPortion;

                        if (FindContainingTag(currentText, selectionStart, selectionEnd, "size", out tagStart, out tagEnd, out beforeSelection, out selectedPortion, out afterSelection))
                        {
                            var existingSize = ExtractTagAttribute(currentText, tagStart, tagEnd, "size");

                            // Split the size tag: keep parts before/after in old size, selected part in new size
                            string replacement = "";

                            // Add the before part in original size (if not empty)
                            if (!string.IsNullOrEmpty(beforeSelection))
                            {
                                replacement += $"<size={existingSize}>{beforeSelection}</size>";
                            }

                            // Add the selected part in new size
                            replacement += $"<size={size}>{selectedPortion}</size>";

                            // Add the after part in original size (if not empty)
                            if (!string.IsNullOrEmpty(afterSelection))
                            {
                                replacement += $"<size={existingSize}>{afterSelection}</size>";
                            }

                            // Replace the entire original size tag with the split version
                            newText = currentText.Substring(0, tagStart) +
                                     replacement +
                                     currentText.Substring(tagEnd);

                            // Calculate new cursor position
                            int replacementOffset = 0;
                            if (!string.IsNullOrEmpty(beforeSelection))
                            {
                                replacementOffset += $"<size={existingSize}>{beforeSelection}</size>".Length;
                            }
                            replacementOffset += $"<size={size}>{selectedPortion}</size>".Length;

                            newCursorPos = tagStart + replacementOffset;
                            newSelectPos = newCursorPos;
                        }
                        else
                        {
                            // Add new size tag around the selected text
                            var sizedText = $"<size={size}>{selectedText}</size>";
                            newText = currentText.Substring(0, selectionStart) +
                                     sizedText +
                                     currentText.Substring(selectionEnd);
                            newCursorPos = selectionStart + sizedText.Length;
                            newSelectPos = newCursorPos;
                        }
                    }
                }
                else
                {
                    // Apply size to entire text - using existing logic
                    var colorPattern = @"^<color=[^>]*>(.*)</color>$";
                    var colorMatch = Regex.Match(currentText, colorPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

                    if (colorMatch.Success)
                    {
                        var innerContent = colorMatch.Groups[1].Value;
                        var sizedInner = $"<size={size}>{innerContent}</size>";
                        newText = currentText.Replace(innerContent, sizedInner);
                    }
                    else
                    {
                        newText = $"<size={size}>{currentText}</size>";
                    }
                    newCursorPos = newText.Length;
                    newSelectPos = newCursorPos;
                }

                // Clear captured selection after use
                hasValidCapturedSelection = false;

                nameField.value = newText;

                EditorApplication.delayCall += () =>
                {
                    if (nameField != null)
                    {
                        nameField.cursorIndex = newCursorPos;
                        nameField.selectIndex = newSelectPos;
                        lastCursorPos = newCursorPos;
                        lastSelectPos = newSelectPos;
                    }
                };

                lastKnownControlName = newText;

                UpdatePreview();
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error applying size: {e}");
            }
        }

        private static void ClearSizes()
        {
            try
            {
                var currentText = nameField.value ?? "";
                int selectionStart, selectionEnd;
                bool hasSelection = GetSelectionRange(out selectionStart, out selectionEnd);

                if (hasSelection && selectionStart >= 0 && selectionEnd <= currentText.Length && selectionStart < selectionEnd)
                {
                    RemoveSizeFromSelection();
                }
                else
                {
                    // Remove all sizes from entire text
                    var cleanText = RemoveSizeTags(currentText);

                    // Record undo
                    if (currentListView != null && currentEditor?.Menu != null)
                    {
                        int selectedIndex = currentListView.selectedIndex;
                        if (selectedIndex >= 0 && selectedIndex < currentEditor.Menu.controls.Count)
                        {
                            Undo.RecordObject(currentEditor.Menu, "Clear Sizes");
                        }
                    }

                    nameField.value = cleanText;

                    EditorApplication.delayCall += () =>
                    {
                        if (nameField != null)
                        {
                            nameField.cursorIndex = cleanText.Length;
                            nameField.selectIndex = cleanText.Length;
                            lastCursorPos = cleanText.Length;
                            lastSelectPos = cleanText.Length;
                        }
                    };

                    lastKnownControlName = cleanText;
                }

                hasValidCapturedSelection = false;
                UpdateToggleButtonStates();
                UpdatePreview();
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error clearing sizes: {e}");
            }
        }

        private static string RemoveSizeTags(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // Remove complete size tag pairs first
            var result = Regex.Replace(text, @"<size=[^>]*>(.*?)</size>", "$1", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // Clean up any remaining orphaned size tags (opening or closing)
            result = Regex.Replace(result, @"</?size[^>]*>", "", RegexOptions.IgnoreCase);

            return result;
        }

        private static string RemoveFormattingTags(string text, string tagName)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // Handle attribute tags (color, size)
            if (tagName == "color")
            {
                var result = Regex.Replace(text, @"<color=[^>]*>(.*?)</color>", "$1", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                result = Regex.Replace(result, @"</?color[^>]*>", "", RegexOptions.IgnoreCase);
                return result;
            }
            else if (tagName == "size")
            {
                return RemoveSizeTags(text); // Use the dedicated method
            }
            else
            {
                // Handle simple tags (b, i, u, s)
                var result = Regex.Replace(text, $@"<{tagName}(\s[^>]*)?>(.*?)</{tagName}>", "$2", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                result = Regex.Replace(result, $@"</?{tagName}(\s[^>]*)??>", "", RegexOptions.IgnoreCase);
                return result;
            }
        }

        private static void RemoveSizeFromSelection()
        {
            try
            {
                var currentText = nameField.value ?? "";
                int selectionStart, selectionEnd;
                bool hasSelection = GetSelectionRange(out selectionStart, out selectionEnd);

                if (!hasSelection) return;

                var selectedText = currentText.Substring(selectionStart, selectionEnd - selectionStart);

                // Check if selection is exactly wrapped in size
                var sizePattern = @"^<size=([^>]*)>(.*)</size>$";
                var exactMatch = Regex.Match(selectedText, sizePattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

                if (exactMatch.Success)
                {
                    var innerContent = exactMatch.Groups[2].Value;

                    ApplyTextChange(
                        currentText.Substring(0, selectionStart) + innerContent + currentText.Substring(selectionEnd),
                        selectionStart + innerContent.Length,
                        "Remove Size"
                    );
                }
                else
                {
                    // Check if selection is within a larger size tag
                    int tagStart, tagEnd;
                    string beforeSelection, afterSelection, selectedPortion;

                    if (FindContainingTag(currentText, selectionStart, selectionEnd, "size", out tagStart, out tagEnd, out beforeSelection, out selectedPortion, out afterSelection))
                    {
                        var existingSize = ExtractTagAttribute(currentText, tagStart, tagEnd, "size");

                        // Split the size tag: keep parts before/after in old size, selected part unsized
                        string replacement = "";

                        if (!string.IsNullOrEmpty(beforeSelection))
                        {
                            replacement += $"<size={existingSize}>{beforeSelection}</size>";
                        }

                        replacement += selectedPortion; // No size tag

                        if (!string.IsNullOrEmpty(afterSelection))
                        {
                            replacement += $"<size={existingSize}>{afterSelection}</size>";
                        }

                        var newText = currentText.Substring(0, tagStart) + replacement + currentText.Substring(tagEnd);

                        int newCursorPos = tagStart;
                        if (!string.IsNullOrEmpty(beforeSelection))
                        {
                            newCursorPos += $"<size={existingSize}>{beforeSelection}</size>".Length;
                        }
                        newCursorPos += selectedPortion.Length;

                        ApplyTextChange(newText, newCursorPos, "Remove Size");
                    }
                }

                hasValidCapturedSelection = false;
                UpdatePreview();
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error removing size from selection: {e}");
            }
        }

        private static void OnColorChanged(ChangeEvent<Color> evt)
        {
            UpdatePreview();
        }

        private static void OnSizeChanged(ChangeEvent<int> evt)
        {
            UpdatePreview();
        }

        private static void ClearFormatting()
        {
            try
            {
                var currentText = nameField.value;

                // Remove all rich text tags using a comprehensive approach
                var cleanText = currentText;

                // Remove specific tag types one by one to ensure complete removal
                cleanText = RemoveFormattingTags(cleanText, "color");
                cleanText = RemoveFormattingTags(cleanText, "size");
                cleanText = RemoveFormattingTags(cleanText, "b");
                cleanText = RemoveFormattingTags(cleanText, "i");
                cleanText = RemoveFormattingTags(cleanText, "u");
                cleanText = RemoveFormattingTags(cleanText, "s");

                // Final cleanup - remove any remaining tags that might have been missed
                cleanText = Regex.Replace(cleanText, @"<[^>]*>", "", RegexOptions.IgnoreCase);

                // Record undo
                if (currentListView != null && currentEditor?.Menu != null)
                {
                    int selectedIndex = currentListView.selectedIndex;
                    if (selectedIndex >= 0 && selectedIndex < currentEditor.Menu.controls.Count)
                    {
                        Undo.RecordObject(currentEditor.Menu, "Clear Formatting");
                    }
                }

                nameField.value = cleanText;

                EditorApplication.delayCall += () =>
                {
                    if (nameField != null)
                    {
                        nameField.cursorIndex = cleanText.Length;
                        nameField.selectIndex = cleanText.Length;
                        lastCursorPos = cleanText.Length;
                        lastSelectPos = cleanText.Length;
                    }
                };

                lastKnownControlName = cleanText;

                UpdateToggleButtonStates();
                UpdatePreview();
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error clearing formatting: {e}");
            }
        }

        private static void UpdatePreview()
        {
            try
            {
                if (previewLabel != null && nameField != null)
                {
                    var text = nameField.value;
                    if (string.IsNullOrEmpty(text))
                    {
                        text = "Sample Text";
                    }

                    previewLabel.text = string.IsNullOrEmpty(nameField.value) ? "Sample Text" : nameField.value;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error updating preview: {e}");
            }
        }

        private static void OnListViewMouseDown(MouseDownEvent evt)
        {
            try
            {
                // Check for double-click
                if (evt.clickCount == 2)
                {
                    // Get the actual clicked item by finding which visual element was clicked
                    var clickedElement = evt.target as VisualElement;
                    var listViewItem = clickedElement;

                    // Walk up the hierarchy to find the list view item
                    while (listViewItem != null && !listViewItem.ClassListContains("unity-list-view__item"))
                    {
                        listViewItem = listViewItem.parent;
                    }

                    if (listViewItem != null)
                    {
                        // Get the index from the item
                        var itemIndex = -1;
                        var listViewItemParent = listViewItem.parent;
                        if (listViewItemParent != null)
                        {
                            for (int i = 0; i < listViewItemParent.childCount; i++)
                            {
                                if (listViewItemParent[i] == listViewItem)
                                {
                                    itemIndex = i;
                                    break;
                                }
                            }
                        }

                        // If we found a valid index, update selection and try to open submenu
                        if (itemIndex >= 0 && itemIndex < currentEditor.Menu.controls.Count)
                        {
                            // Update the selection to the clicked item
                            currentListView.selectedIndex = itemIndex;

                            // Try to open the submenu
                            OpenSelectedSubmenu();
                        }
                    }
                    else
                    {
                        // Fallback to the original method
                        OpenSelectedSubmenu();
                    }

                    evt.StopPropagation();
                    evt.PreventDefault();
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error handling double-click: {e}");
            }
        }

        private static void OnKeyDown(KeyDownEvent evt)
        {
            try
            {
                // Check if we have focus and a valid menu
                if (currentEditor?.Menu == null || currentListView == null) return;

                bool ctrlPressed = evt.ctrlKey || evt.commandKey; // Command key for Mac support

                switch (evt.keyCode)
                {
                    case KeyCode.Delete:
                        if (CanDeleteSelected())
                        {
                            DeleteSelectedControl();
                            evt.StopPropagation();
                            evt.PreventDefault();
                        }
                        break;

                    case KeyCode.Return:
                    case KeyCode.KeypadEnter:
                        if (CanOpenSelectedSubmenu())
                        {
                            OpenSelectedSubmenu();
                            evt.StopPropagation();
                            evt.PreventDefault();
                        }
                        break;

                    case KeyCode.C:
                        if (ctrlPressed && CanCopySelected())
                        {
                            CopySelectedControl();
                            evt.StopPropagation();
                            evt.PreventDefault();
                        }
                        break;

                    case KeyCode.V:
                        if (ctrlPressed && CanPaste())
                        {
                            ShowPasteOptionsWithKeyboard();
                            evt.StopPropagation();
                            evt.PreventDefault();
                        }
                        break;

                    case KeyCode.D:
                        if (ctrlPressed && CanDuplicateSelected())
                        {
                            DuplicateSelectedControl();
                            evt.StopPropagation();
                            evt.PreventDefault();
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error handling keyboard shortcut: {e}");
            }
        }

        private static void AddToNavigationHistory(VRCExpressionsMenu menu)
        {
            if (menu == null) return;

            // Don't add the same menu twice in a row
            if (navigationHistory.Count > 0 && navigationHistory[navigationHistory.Count - 1] == menu)
            {
                // Just update the current index if we're not at the end
                if (currentHistoryIndex < navigationHistory.Count - 1)
                {
                    currentHistoryIndex = navigationHistory.Count - 1;
                }
                return;
            }

            // If we're not at the end of history and this is a new manual navigation,
            // remove everything after current position to create a clean new branch
            if (currentHistoryIndex >= 0 && currentHistoryIndex < navigationHistory.Count - 1 && !isNavigatingProgrammatically)
            {
                navigationHistory.RemoveRange(currentHistoryIndex + 1, navigationHistory.Count - currentHistoryIndex - 1);
            }

            navigationHistory.Add(menu);
            currentHistoryIndex = navigationHistory.Count - 1;

            // Keep history reasonable size (max 50 items)
            if (navigationHistory.Count > 50)
            {
                navigationHistory.RemoveAt(0);
                currentHistoryIndex--;
            }

            // Update button states after history changes
            UpdateButtonStates();
        }

        private static void NavigateBack()
        {
            try
            {
                if (currentHistoryIndex > 0)
                {
                    currentHistoryIndex--;
                    var targetMenu = navigationHistory[currentHistoryIndex];
                    if (targetMenu != null)
                    {
                        isNavigatingProgrammatically = true;
                        Selection.activeObject = targetMenu;
                        // Reset flag after a short delay to allow the selection to process
                        EditorApplication.delayCall += () => {
                            isNavigatingProgrammatically = false;
                            UpdateButtonStates();
                        };
                    }
                }
                UpdateButtonStates();
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error navigating back: {e}");
            }
        }

        private static void NavigateForward()
        {
            try
            {
                if (currentHistoryIndex < navigationHistory.Count - 1)
                {
                    currentHistoryIndex++;
                    var targetMenu = navigationHistory[currentHistoryIndex];
                    if (targetMenu != null)
                    {
                        isNavigatingProgrammatically = true;
                        Selection.activeObject = targetMenu;
                        // Reset flag after a short delay to allow the selection to process
                        EditorApplication.delayCall += () => {
                            isNavigatingProgrammatically = false;
                            UpdateButtonStates();
                        };
                    }
                }
                UpdateButtonStates();
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error navigating forward: {e}");
            }
        }

        private static bool CanOpenSelectedSubmenu()
        {
            if (currentListView == null || currentEditor?.Menu == null) return false;

            int selectedIndex = currentListView.selectedIndex;
            if (selectedIndex < 0 || selectedIndex >= currentEditor.Menu.controls.Count) return false;

            var selectedControl = currentEditor.Menu.controls[selectedIndex];
            return selectedControl.type == VRCExpressionsMenu.Control.ControlType.SubMenu &&
                   selectedControl.subMenu != null;
        }

        private static void OpenSelectedSubmenu()
        {
            try
            {
                if (!CanOpenSelectedSubmenu()) return;

                int selectedIndex = currentListView.selectedIndex;
                var selectedControl = currentEditor.Menu.controls[selectedIndex];

                if (selectedControl.subMenu != null)
                {
                    // Clean up navigation history when navigating to submenu manually
                    // Remove any "forward" history entries since we're creating a new branch
                    if (currentHistoryIndex >= 0 && currentHistoryIndex < navigationHistory.Count - 1)
                    {
                        navigationHistory.RemoveRange(currentHistoryIndex + 1, navigationHistory.Count - currentHistoryIndex - 1);
                    }

                    // Navigate to submenu - this will trigger AddToNavigationHistory normally
                    Selection.activeObject = selectedControl.subMenu;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error opening submenu: {e}");
            }
        }

        private static bool CanDeleteSelected()
        {
            if (currentListView == null || currentEditor?.Menu == null) return false;
            int selectedIndex = currentListView.selectedIndex;
            return selectedIndex >= 0 && selectedIndex < currentEditor.Menu.controls.Count;
        }

        private static bool CanCopySelected()
        {
            return CanDeleteSelected(); // Same condition as delete
        }

        private static bool CanPaste()
        {
            if (currentEditor?.Menu == null) return false;
            bool hasClipboardData = copiedControl != null || CanPasteFromClipboard();

            // Can paste if we have data and either have room for new items OR have a selection to replace
            bool canPasteNew = hasClipboardData && currentEditor.Menu.controls.Count < VRCExpressionsMenu.MAX_CONTROLS;
            bool canPasteReplace = hasClipboardData && CanDeleteSelected();

            return canPasteNew || canPasteReplace;
        }

        private static bool CanDuplicateSelected()
        {
            if (!CanDeleteSelected()) return false;
            return currentEditor.Menu.controls.Count < VRCExpressionsMenu.MAX_CONTROLS;
        }

        private static void DeleteSelectedControl()
        {
            try
            {
                if (!CanDeleteSelected()) return;

                int selectedIndex = currentListView.selectedIndex;
                var menu = currentEditor.Menu;

                // Record undo
                Undo.RecordObject(menu, "Delete Expression Menu Control");

                menu.controls.RemoveAt(selectedIndex);
                EditorUtility.SetDirty(menu);

                // Update selection to next item or previous if we deleted the last one
                int newSelection = selectedIndex;
                if (newSelection >= menu.controls.Count)
                {
                    newSelection = menu.controls.Count - 1;
                }

                currentListView.selectedIndex = newSelection;
                UpdateButtonStates();
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error deleting control: {e}");
            }
        }

        private static void ShowPasteOptionsWithKeyboard()
        {
            try
            {
                if (currentEditor?.Menu == null) return;

                var menu = currentEditor.Menu;
                bool hasSelection = currentListView?.selectedIndex >= 0 &&
                                  currentListView.selectedIndex < menu.controls.Count;
                bool canPasteNew = menu.controls.Count < VRCExpressionsMenu.MAX_CONTROLS;

                // Create a generic menu for the dropdown
                var genericMenu = new GenericMenu();

                // Add "Insert New" option if possible
                if (canPasteNew)
                {
                    genericMenu.AddItem(
                        new GUIContent("Insert New"),
                        false,
                        () => PasteControlAsNew()
                    );
                }
                else
                {
                    genericMenu.AddDisabledItem(new GUIContent("Insert New"));
                }

                // Add "Replace Selected" option if something is selected
                if (hasSelection)
                {
                    genericMenu.AddItem(
                        new GUIContent("Replace Selected"),
                        false,
                        () => PasteControlAsReplace()
                    );
                }
                else
                {
                    genericMenu.AddDisabledItem(new GUIContent("Replace Selected"));
                }

                // Show the dropdown menu at mouse position when triggered by keyboard
                var mousePosition = Event.current?.mousePosition ?? Vector2.zero;
                genericMenu.ShowAsContext();
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error showing paste options: {e}");
            }
        }

        private static void ShowMakeUniqueConfirmation()
        {
            try
            {
                if (currentEditor?.Menu == null) return;

                var menu = currentEditor.Menu;
                var menuPath = AssetDatabase.GetAssetPath(menu);

                if (string.IsNullOrEmpty(menuPath))
                {
                    Debug.LogWarning("[SDK@Home] Cannot make unique: Menu is not saved as an asset");
                    return;
                }

                // Count how many submenus would be affected
                var allSubmenus = new HashSet<VRCExpressionsMenu>();
                CollectAllSubmenus(menu, allSubmenus);

                string message = $"This will create a unique copy of the entire menu hierarchy:\n\n" +
                                $"• Root menu: {menu.name} → {menu.name}_Copy\n" +
                                $"• Submenus: {allSubmenus.Count} copies will be created\n" +
                                $"• Location: {Path.GetDirectoryName(menuPath)}/{menu.name}_Unique/\n\n" +
                                "All references will be updated to use the new copies.\n" +
                                "The new root menu will be selected when complete.\n\n" +
                                "This operation cannot be undone. Continue?";

                if (EditorUtility.DisplayDialog("Make Expression Menu Unique", message, "Make Unique", "Cancel"))
                {
                    MakeMenuUnique(menu);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error showing make unique confirmation: {e}");
            }
        }

        private static void MakeMenuUnique(VRCExpressionsMenu rootMenu)
        {
            try
            {
                var menuPath = AssetDatabase.GetAssetPath(rootMenu);
                var menuDirectory = Path.GetDirectoryName(menuPath);
                var uniqueFolderName = $"{rootMenu.name}_Unique";
                var uniqueFolderPath = Path.Combine(menuDirectory, uniqueFolderName);

                // Create unique folder
                if (!Directory.Exists(uniqueFolderPath))
                {
                    Directory.CreateDirectory(uniqueFolderPath);
                    AssetDatabase.Refresh();
                }

                // Collect all submenus that need to be copied
                var allSubmenus = new HashSet<VRCExpressionsMenu>();
                CollectAllSubmenus(rootMenu, allSubmenus);

                // Create mapping from original to copy (including the root menu)
                var menuMapping = new Dictionary<VRCExpressionsMenu, VRCExpressionsMenu>();

                // First, create a copy of the root menu itself
                var rootFileName = Path.GetFileNameWithoutExtension(menuPath);
                var rootExtension = Path.GetExtension(menuPath);
                var rootCopyPath = Path.Combine(uniqueFolderPath, $"{rootFileName}{rootExtension}").Replace("\\", "/");
                rootCopyPath = AssetDatabase.GenerateUniqueAssetPath(rootCopyPath);

                if (AssetDatabase.CopyAsset(menuPath, rootCopyPath))
                {
                    AssetDatabase.Refresh();
                    var copiedRootMenu = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(rootCopyPath);
                    if (copiedRootMenu != null)
                    {
                        menuMapping[rootMenu] = copiedRootMenu;
                    }
                    else
                    {
                        return;
                    }
                }
                else
                {
                    return;
                }

                // Create copies of all submenus
                foreach (var submenu in allSubmenus)
                {
                    var originalPath = AssetDatabase.GetAssetPath(submenu);
                    var fileName = Path.GetFileNameWithoutExtension(originalPath);
                    var extension = Path.GetExtension(originalPath);
                    var newPath = Path.Combine(uniqueFolderPath, $"{fileName}{extension}").Replace("\\", "/");

                    // Make sure path is unique
                    newPath = AssetDatabase.GenerateUniqueAssetPath(newPath);

                    // Copy the asset
                    if (AssetDatabase.CopyAsset(originalPath, newPath))
                    {
                        AssetDatabase.Refresh();
                        var copiedMenu = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(newPath);
                        if (copiedMenu != null)
                        {
                            menuMapping[submenu] = copiedMenu;
                        }
                    }
                    else
                    {
                        Debug.LogError($"[SDK@Home] Failed to copy {originalPath} to {newPath}");
                    }
                }

                // Update all references in the copied menus (including the copied root menu)
                foreach (var kvp in menuMapping)
                {
                    var copiedMenu = kvp.Value;
                    UpdateMenuReferences(copiedMenu, menuMapping);
                }

                // Mark all copied assets as dirty and save
                foreach (var copiedMenu in menuMapping.Values)
                {
                    EditorUtility.SetDirty(copiedMenu);
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                // Select the new copied root menu
                var newRootMenu = menuMapping[rootMenu];
                Selection.activeObject = newRootMenu;
                EditorGUIUtility.PingObject(newRootMenu);
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error making menu unique: {e}");
                EditorUtility.DisplayDialog("Error", $"Failed to make menu unique: {e.Message}", "OK");
            }
        }

        private static void CollectAllSubmenus(VRCExpressionsMenu menu, HashSet<VRCExpressionsMenu> collectedMenus, HashSet<VRCExpressionsMenu> visited = null)
        {
            if (menu == null) return;

            // Prevent infinite recursion
            if (visited == null) visited = new HashSet<VRCExpressionsMenu>();
            if (visited.Contains(menu)) return;
            visited.Add(menu);

            if (menu.controls == null) return;

            foreach (var control in menu.controls)
            {
                if (control.type == VRCExpressionsMenu.Control.ControlType.SubMenu && control.subMenu != null)
                {
                    // Add the submenu to our collection
                    collectedMenus.Add(control.subMenu);

                    // Recursively collect submenus from this submenu
                    CollectAllSubmenus(control.subMenu, collectedMenus, visited);
                }
            }
        }

        private static void UpdateMenuReferences(VRCExpressionsMenu menu, Dictionary<VRCExpressionsMenu, VRCExpressionsMenu> menuMapping)
        {
            if (menu?.controls == null) return;

            bool menuModified = false;

            for (int i = 0; i < menu.controls.Count; i++)
            {
                var control = menu.controls[i];
                if (control.type == VRCExpressionsMenu.Control.ControlType.SubMenu && control.subMenu != null)
                {
                    // Check if we have a copied version of this submenu
                    if (menuMapping.TryGetValue(control.subMenu, out var copiedSubmenu))
                    {
                        // Update the reference to point to the copied version
                        control.subMenu = copiedSubmenu;
                        menuModified = true;
                    }
                }
            }

            if (menuModified)
            {
                EditorUtility.SetDirty(menu);
            }
        }

        private static void UpdateButtonStates()
        {
            try
            {
                if (currentListView == null || currentEditor?.Menu == null) return;

                int selectedIndex = currentListView.selectedIndex;
                var menu = currentEditor.Menu;
                bool hasSelection = selectedIndex >= 0 && selectedIndex < menu.controls.Count;
                bool canPaste = copiedControl != null || CanPasteFromClipboard();
                bool canAddMore = menu.controls.Count < VRCExpressionsMenu.MAX_CONTROLS;

                if (copyButton != null)
                    copyButton.SetEnabled(hasSelection);

                if (pasteButton != null)
                {
                    bool canPasteNew = canPaste && canAddMore;
                    bool canPasteReplace = canPaste && hasSelection;
                    pasteButton.SetEnabled(canPasteNew || canPasteReplace);
                }

                if (duplicateButton != null)
                    duplicateButton.SetEnabled(hasSelection && canAddMore);

                if (backButton != null)
                    backButton.SetEnabled(currentHistoryIndex > 0);

                if (forwardButton != null)
                    forwardButton.SetEnabled(currentHistoryIndex < navigationHistory.Count - 1);

                // Enable Make Unique button when we have a valid menu that's saved as an asset
                if (makeUniqueButton != null)
                {
                    var menuPath = AssetDatabase.GetAssetPath(currentEditor.Menu);
                    makeUniqueButton.SetEnabled(!string.IsNullOrEmpty(menuPath));
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error updating button states: {e}");
            }
        }

        private static void ShowPasteOptions()
        {
            try
            {
                if (currentEditor?.Menu == null) return;

                var menu = currentEditor.Menu;
                bool hasSelection = currentListView?.selectedIndex >= 0 &&
                                  currentListView.selectedIndex < menu.controls.Count;

                // Create a generic menu for the dropdown
                var genericMenu = new GenericMenu();

                // Add "Insert New" option
                genericMenu.AddItem(
                    new GUIContent("Insert New"),
                    false,
                    () => PasteControlAsNew()
                );

                // Add "Replace Selected" option only if something is selected
                if (hasSelection)
                {
                    genericMenu.AddItem(
                        new GUIContent("Replace Selected"),
                        false,
                        () => PasteControlAsReplace()
                    );
                }
                else
                {
                    genericMenu.AddDisabledItem(new GUIContent("Replace Selected"));
                }

                // Show the dropdown menu at the button position
                var buttonRect = pasteButton.worldBound;
                genericMenu.DropDown(new Rect(buttonRect.x, buttonRect.y + buttonRect.height, 0, 0));
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error showing paste options: {e}");
            }
        }

        private static void CopySelectedControl()
        {
            try
            {
                if (currentListView == null || currentEditor?.Menu == null) return;

                int selectedIndex = currentListView.selectedIndex;
                var menu = currentEditor.Menu;

                if (selectedIndex >= 0 && selectedIndex < menu.controls.Count)
                {
                    CopyControl(menu.controls[selectedIndex]);
                    UpdateButtonStates();
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error copying control: {e}");
            }
        }

        private static void PasteControlAsNew()
        {
            try
            {
                if (currentEditor?.Menu == null) return;

                var menu = currentEditor.Menu;
                int insertIndex = currentListView?.selectedIndex >= 0 ?
                    currentListView.selectedIndex + 1 : menu.controls.Count;

                PasteControl(menu, insertIndex, false);
                UpdateButtonStates();
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error pasting as new control: {e}");
            }
        }

        private static void PasteControlAsReplace()
        {
            try
            {
                if (currentListView == null || currentEditor?.Menu == null) return;

                int selectedIndex = currentListView.selectedIndex;
                var menu = currentEditor.Menu;

                if (selectedIndex >= 0 && selectedIndex < menu.controls.Count)
                {
                    PasteControl(menu, selectedIndex, true);
                    UpdateButtonStates();
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error pasting as replacement: {e}");
            }
        }

        private static void DuplicateSelectedControl()
        {
            try
            {
                if (currentListView == null || currentEditor?.Menu == null) return;

                int selectedIndex = currentListView.selectedIndex;
                var menu = currentEditor.Menu;

                if (selectedIndex >= 0 && selectedIndex < menu.controls.Count)
                {
                    DuplicateControl(menu, selectedIndex);
                    UpdateButtonStates();
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Error duplicating control: {e}");
            }
        }

        // Legacy method - now just calls PasteControlAsNew for backwards compatibility
        private static void PasteControl()
        {
            PasteControlAsNew();
        }

        private static bool CanPasteFromClipboard()
        {
            return !string.IsNullOrEmpty(EditorGUIUtility.systemCopyBuffer) &&
                   EditorGUIUtility.systemCopyBuffer.StartsWith("VRCExpressionControl:");
        }

        private static void CopyControl(VRCExpressionsMenu.Control control)
        {
            try
            {
                // Deep copy the control
                copiedControl = DeepCopyControl(control);

                // Also copy to system clipboard as JSON for cross-session persistence
                var json = JsonUtility.ToJson(new SerializableControl(control));
                EditorGUIUtility.systemCopyBuffer = $"VRCExpressionControl:{json}";
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Failed to copy control: {e}");
            }
        }

        private static void PasteControl(VRCExpressionsMenu menu, int insertIndex = -1, bool replaceMode = false)
        {
            try
            {
                VRCExpressionsMenu.Control controlToPaste = null;

                // Try to get from in-memory copy first
                if (copiedControl != null)
                {
                    controlToPaste = DeepCopyControl(copiedControl);
                }
                // Fallback to system clipboard
                else if (EditorGUIUtility.systemCopyBuffer.StartsWith("VRCExpressionControl:"))
                {
                    var json = EditorGUIUtility.systemCopyBuffer.Substring("VRCExpressionControl:".Length);
                    var serializableControl = JsonUtility.FromJson<SerializableControl>(json);
                    controlToPaste = serializableControl.ToControl();
                }

                if (controlToPaste == null)
                {
                    return;
                }

                // Record undo before making changes
                Undo.RecordObject(menu, replaceMode ? "Replace Expression Menu Control" : "Paste Expression Menu Control");

                if (replaceMode)
                {
                    // Replace mode: replace the control at the specified index
                    if (insertIndex >= 0 && insertIndex < menu.controls.Count)
                    {
                        menu.controls[insertIndex] = controlToPaste;
                    }
                    else
                    {
                        return;
                    }
                }
                else
                {
                    // Insert mode: add new control
                    if (menu.controls.Count >= VRCExpressionsMenu.MAX_CONTROLS)
                    {
                        return;
                    }

                    // Insert at specified index or append
                    if (insertIndex < 0 || insertIndex >= menu.controls.Count)
                    {
                        menu.controls.Add(controlToPaste);
                    }
                    else
                    {
                        menu.controls.Insert(insertIndex, controlToPaste);
                    }
                }

                // Mark as dirty for saving
                EditorUtility.SetDirty(menu);
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Failed to paste control: {e}");
            }
        }

        private static void DuplicateControl(VRCExpressionsMenu menu, int index)
        {
            try
            {
                if (index < 0 || index >= menu.controls.Count) return;
                if (menu.controls.Count >= VRCExpressionsMenu.MAX_CONTROLS)
                {
                    return;
                }

                // Record undo
                Undo.RecordObject(menu, "Duplicate Expression Menu Control");

                var original = menu.controls[index];
                var duplicate = DeepCopyControl(original);
                // Removed the " (Copy)" suffix as requested

                menu.controls.Insert(index + 1, duplicate);
                EditorUtility.SetDirty(menu);

                // Select the newly duplicated item
                currentListView.selectedIndex = index + 1;
            }
            catch (Exception e)
            {
                Debug.LogError($"[SDK@Home] Failed to duplicate control: {e}");
            }
        }

        private static VRCExpressionsMenu.Control DeepCopyControl(VRCExpressionsMenu.Control original)
        {
            var copy = new VRCExpressionsMenu.Control
            {
                name = original.name,
                icon = original.icon,
                type = original.type,
                value = original.value,
                style = original.style,
                subMenu = original.subMenu,
                parameter = original.parameter != null ? new VRCExpressionsMenu.Control.Parameter { name = original.parameter.name } : null
            };

            // Copy sub-parameters array
            if (original.subParameters != null)
            {
                copy.subParameters = new VRCExpressionsMenu.Control.Parameter[original.subParameters.Length];
                for (int i = 0; i < original.subParameters.Length; i++)
                {
                    if (original.subParameters[i] != null)
                    {
                        copy.subParameters[i] = new VRCExpressionsMenu.Control.Parameter
                        {
                            name = original.subParameters[i].name
                        };
                    }
                }
            }

            // Copy labels array
            if (original.labels != null)
            {
                copy.labels = new VRCExpressionsMenu.Control.Label[original.labels.Length];
                for (int i = 0; i < original.labels.Length; i++)
                {
                    copy.labels[i] = new VRCExpressionsMenu.Control.Label
                    {
                        name = original.labels[i].name,
                        icon = original.labels[i].icon
                    };
                }
            }

            return copy;
        }

        // Rest of the serialization classes remain the same...
        [Serializable]
        private class SerializableControl
        {
            public string name;
            public string iconPath;
            public VRCExpressionsMenu.Control.ControlType type;
            public float value;
            public VRCExpressionsMenu.Control.Style style;
            public string subMenuPath;
            public SerializableParameter parameter;
            public SerializableParameter[] subParameters;
            public SerializableLabel[] labels;

            public SerializableControl(VRCExpressionsMenu.Control control)
            {
                name = control.name;
                iconPath = control.icon != null ? AssetDatabase.GetAssetPath(control.icon) : null;
                type = control.type;
                value = control.value;
                style = control.style;
                subMenuPath = control.subMenu != null ? AssetDatabase.GetAssetPath(control.subMenu) : null;

                parameter = control.parameter != null ? new SerializableParameter(control.parameter) : null;

                if (control.subParameters != null)
                {
                    subParameters = new SerializableParameter[control.subParameters.Length];
                    for (int i = 0; i < control.subParameters.Length; i++)
                    {
                        subParameters[i] = control.subParameters[i] != null ?
                            new SerializableParameter(control.subParameters[i]) : null;
                    }
                }

                if (control.labels != null)
                {
                    labels = new SerializableLabel[control.labels.Length];
                    for (int i = 0; i < control.labels.Length; i++)
                    {
                        labels[i] = new SerializableLabel(control.labels[i]);
                    }
                }
            }

            public VRCExpressionsMenu.Control ToControl()
            {
                var control = new VRCExpressionsMenu.Control
                {
                    name = name,
                    type = type,
                    value = value,
                    style = style
                };

                // Load icon if path exists
                if (!string.IsNullOrEmpty(iconPath))
                {
                    control.icon = AssetDatabase.LoadAssetAtPath<Texture2D>(iconPath);
                }

                // Load submenu if path exists
                if (!string.IsNullOrEmpty(subMenuPath))
                {
                    control.subMenu = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(subMenuPath);
                }

                // Convert parameter
                control.parameter = parameter?.ToParameter();

                // Convert sub-parameters
                if (subParameters != null)
                {
                    control.subParameters = new VRCExpressionsMenu.Control.Parameter[subParameters.Length];
                    for (int i = 0; i < subParameters.Length; i++)
                    {
                        control.subParameters[i] = subParameters[i]?.ToParameter();
                    }
                }

                // Convert labels
                if (labels != null)
                {
                    control.labels = new VRCExpressionsMenu.Control.Label[labels.Length];
                    for (int i = 0; i < labels.Length; i++)
                    {
                        control.labels[i] = labels[i].ToLabel();
                    }
                }

                return control;
            }
        }

        [Serializable]
        private class SerializableParameter
        {
            public string name;

            public SerializableParameter(VRCExpressionsMenu.Control.Parameter param)
            {
                name = param.name;
            }

            public VRCExpressionsMenu.Control.Parameter ToParameter()
            {
                return new VRCExpressionsMenu.Control.Parameter { name = name };
            }
        }

        [Serializable]
        private class SerializableLabel
        {
            public string name;
            public string iconPath;

            public SerializableLabel(VRCExpressionsMenu.Control.Label label)
            {
                name = label.name;
                iconPath = label.icon != null ? AssetDatabase.GetAssetPath(label.icon) : null;
            }

            public VRCExpressionsMenu.Control.Label ToLabel()
            {
                var label = new VRCExpressionsMenu.Control.Label { name = name };
                if (!string.IsNullOrEmpty(iconPath))
                {
                    label.icon = AssetDatabase.LoadAssetAtPath<Texture2D>(iconPath);
                }
                return label;
            }
        }
    }
}
#endif