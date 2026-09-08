#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace SDKatHome
{
    public class FolderedClipDropdownWindow : EditorWindow
    {
        private Vector2 scrollPosition;

        // Covers a dot and an underscore so the preview reacts to either separator being toggled.
        private const string ExampleClipName = "Toggle.Hat_On";

        private static readonly GUIContent[] MenuStyleOptions =
        {
            new GUIContent("Native context menu", "Unity's standard OS dropdown."),
            new GUIContent("Unity advanced dropdown", "Unity's AdvancedDropdown, with search."),
            new GUIContent("SDK at Home dropdown", "Custom dropdown with search.")
        };

        public static void ShowWindow()
        {
            var window = GetWindow<FolderedClipDropdownWindow>("Clip Dropdown");
            window.minSize = new Vector2(420, 460);
            window.Show();
        }

        private void OnEnable()
        {
            if (!Patches.FolderedClipDropdown.prefsLoaded)
                Patches.FolderedClipDropdown.LoadPreferences();
        }

        private void OnGUI()
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            EditorGUILayout.LabelField("Foldered Animation Clip Dropdown", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Organizes animations into folders", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space();

            EditorGUI.BeginChangeCheck();

            DrawMenuStyle();
            EditorGUILayout.Space();
            DrawFoldCharacters();
            EditorGUILayout.Space();
            DrawGrouping();
            EditorGUILayout.Space();
            DrawPreview();

            if (EditorGUI.EndChangeCheck())
                Patches.FolderedClipDropdown.SavePreferences();

            EditorGUILayout.Space();
            DrawFooter();

            EditorGUILayout.EndScrollView();
        }

        private void DrawMenuStyle()
        {
            EditorGUILayout.LabelField("Menu Style", EditorStyles.boldLabel);

            Patches.FolderedClipDropdown.menuStyle = EditorGUILayout.Popup(
                new GUIContent("Dropdown", "How the clip list is presented when you click it."),
                Patches.FolderedClipDropdown.menuStyle,
                MenuStyleOptions);

            if (Patches.FolderedClipDropdown.menuStyle == Patches.FolderedClipDropdown.MenuStyleAdvanced)
            {
                EditorGUI.indentLevel++;
                Patches.FolderedClipDropdown.sortFoldersFirst = EditorGUILayout.Toggle(
                    new GUIContent("Folders first", "List submenus above clips that have no folder."),
                    Patches.FolderedClipDropdown.sortFoldersFirst);
                EditorGUI.indentLevel--;

                EditorGUILayout.HelpBox(
                    "The advanced dropdown opens as a window rather than an OS menu, so it can be " +
                    "searched and navigated with the keyboard.",
                    MessageType.None);
            }
        }

        private void DrawFoldCharacters()
        {
            EditorGUILayout.LabelField("Split On", EditorStyles.boldLabel);

            string current = Patches.FolderedClipDropdown.foldCharacters ?? "";

            EditorGUILayout.BeginHorizontal();
            bool dot = ToggleChar(current, '.', "Dot", "Toggle.Hat.On");
            bool underscore = ToggleChar(current, '_', "Underscore", "Toggle_Hat_On");
            bool space = ToggleChar(current, ' ', "Space", "Toggle Hat On");
            bool dash = ToggleChar(current, '-', "Dash", "Toggle-Hat-On");
            EditorGUILayout.EndHorizontal();

            string rebuilt = "";
            if (dot) rebuilt += ".";
            if (underscore) rebuilt += "_";
            if (space) rebuilt += " ";
            if (dash) rebuilt += "-";

            // Preserve any characters the user typed by hand that aren't one of the four presets.
            foreach (char c in current)
            {
                if (c == '.' || c == '_' || c == ' ' || c == '-') continue;
                if (rebuilt.IndexOf(c) < 0) rebuilt += c;
            }

            if (rebuilt != current)
                Patches.FolderedClipDropdown.foldCharacters = rebuilt;

            EditorGUILayout.Space(2);

            string typed = EditorGUILayout.TextField(
                new GUIContent("Characters", "Every character here becomes a folder separator. Combine freely."),
                Patches.FolderedClipDropdown.foldCharacters ?? "");

            // '/' is already the separator, so accepting it would silently do nothing.
            typed = typed.Replace("/", "");
            if (typed != Patches.FolderedClipDropdown.foldCharacters)
                Patches.FolderedClipDropdown.foldCharacters = typed;

            if (string.IsNullOrEmpty(Patches.FolderedClipDropdown.foldCharacters) &&
                !Patches.FolderedClipDropdown.groupByAssetFolder)
            {
                EditorGUILayout.HelpBox(
                    "No split characters and no folder grouping - the dropdown will stay flat.",
                    MessageType.Info);
            }
        }

        private bool ToggleChar(string current, char c, string label, string tooltip)
        {
            bool on = current.IndexOf(c) >= 0;
            return GUILayout.Toggle(on, new GUIContent(label, tooltip), EditorStyles.miniButton);
        }

        private void DrawGrouping()
        {
            EditorGUILayout.LabelField("Grouping", EditorStyles.boldLabel);

            Patches.FolderedClipDropdown.groupByAssetFolder = EditorGUILayout.Toggle(
                new GUIContent("Group by asset folder",
                    "Nest each clip under the project folder its asset lives in."),
                Patches.FolderedClipDropdown.groupByAssetFolder);
        }

        private void DrawPreview()
        {
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);

            string folded = Patches.FolderedClipDropdown.PreviewFold(ExampleClipName);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(ExampleClipName, EditorStyles.miniLabel);
            if (folded == ExampleClipName)
                EditorGUILayout.LabelField("stays flat", EditorStyles.boldLabel);
            else
                EditorGUILayout.LabelField(folded.Replace("/", "  ▸  "), EditorStyles.boldLabel);
            EditorGUILayout.EndVertical();
        }

        private void DrawFooter()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Reset to Defaults", GUILayout.Width(140)))
                ResetToDefaults();
            EditorGUILayout.EndHorizontal();
        }

        private void ResetToDefaults()
        {
            Patches.FolderedClipDropdown.menuStyle = Patches.FolderedClipDropdown.MenuStyleNative;
            Patches.FolderedClipDropdown.foldCharacters = ".";
            Patches.FolderedClipDropdown.groupByAssetFolder = false;
            Patches.FolderedClipDropdown.sortFoldersFirst = true;
            Patches.FolderedClipDropdown.SavePreferences();
            Repaint();
        }
    }
}
#endif
