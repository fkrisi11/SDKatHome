#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace SDKatHome.Patches
{
    /// <summary>
    /// Turns the Animation window's flat clip dropdown into a nested, foldered menu, optionally
    /// rendered with Unity's searchable AdvancedDropdown instead of the native context menu.
    ///
    /// How folding works:
    ///   EditorUtility.DisplayCustomMenu already treats '/' in an option's label as a submenu
    ///   separator, and the callback still receives the index into the ORIGINAL options array -
    ///   the nesting is display-only. So folding is just a matter of rewriting the clip labels
    ///   before the menu is built. "Toggle.Hat.On" becomes "Toggle/Hat/On", rendered as
    ///   Toggle > Hat > On.
    ///
    /// Where we hook, and why NOT the obvious place:
    ///   The tempting target is AnimationWindowClipPopup.GetClipMenuContent, which builds the
    ///   GUIContent[] and conveniently receives the AnimationClip[] as a parameter. Do not patch
    ///   it. Its body contains
    ///
    ///       ldsfld UnityEditor.AnimationWindowStyles::createNewClip
    ///
    ///   and when Harmony prepares the method, the runtime eagerly runs the AnimationWindowStyles
    ///   static constructor. Patches are applied from EditorApplication.delayCall, i.e. outside any
    ///   GUI context, where EditorStyles.s_Current is null - so the cctor throws a
    ///   NullReferenceException on EditorStyles.toolbarButtonRight. A failed static constructor
    ///   POISONS THE TYPE for the lifetime of the domain, and every later AnimationWindow.OnGUI
    ///   rethrows it as TypeInitializationException. The Animation window stops working entirely.
    ///
    ///   AnimationWindowClipPopup.DisplayClipMenu has no reference to AnimationWindowStyles, so it
    ///   is safe to patch. We use it purely as a scope marker: its prefix opens a window during
    ///   which our EditorUtility.DisplayCustomMenu interceptor takes over, and its postfix closes
    ///   that window again. Every other caller of DisplayCustomMenu passes through untouched.
    ///
    /// We never mutate the incoming GUIContent objects. Unity appends two shared statics past the
    /// clip entries - GUIContent.none and AnimationWindowStyles.createNewClip - and the latter
    /// reads "Create New Clip...". A blanket '.' -> '/' replace would rewrite that shared instance
    /// to "Create New Clip///" permanently, for every Animation window in the session.
    /// </summary>
    [HarmonyPatch]
    public class FolderedClipDropdown : SDKPatchBase
    {
        public override string PatchName => "Foldered Animation Clip Dropdown";

        public override string Description => "Organizes animations into folders";

        public override string Category => "Animator Tools";

        public override bool UsePrefix => true;
        public override bool UsePostfix => true;
        public override bool EnabledByDefault => true;

        public override string ButtonText => "Configure";
        public override string ButtonActionMethodName => "FolderedClipDropdownWindow.ShowWindow";

        #region Settings

        public const int MenuStyleNative = 0;
        public const int MenuStyleAdvanced = 1;
        public const int MenuStyleSearchable = 2;

        private const string PREF_MENU_STYLE = "SDKatHome_FolderedClipDropdown_MenuStyle";
        private const string PREF_FOLD_CHARS = "SDKatHome_FolderedClipDropdown_FoldChars";
        private const string PREF_GROUP_BY_FOLDER = "SDKatHome_FolderedClipDropdown_GroupByFolder";
        private const string PREF_SORT_FOLDERS_FIRST = "SDKatHome_FolderedClipDropdown_SortFoldersFirst";

        /// <summary>Native context menu, or Unity's searchable AdvancedDropdown.</summary>
        public static int menuStyle = MenuStyleNative;

        /// <summary>Every character in here becomes a folder separator. "._" folds on both.</summary>
        public static string foldCharacters = ".";

        /// <summary>Additionally nest each clip under the folder its asset lives in.</summary>
        public static bool groupByAssetFolder = false;

        /// <summary>AdvancedDropdown only: list submenus above loose clips.</summary>
        public static bool sortFoldersFirst = true;

        public static bool prefsLoaded = false;

        public static void LoadPreferences()
        {
            menuStyle = EditorPrefs.GetInt(PREF_MENU_STYLE, MenuStyleNative);
            foldCharacters = EditorPrefs.GetString(PREF_FOLD_CHARS, ".");
            groupByAssetFolder = EditorPrefs.GetBool(PREF_GROUP_BY_FOLDER, false);
            sortFoldersFirst = EditorPrefs.GetBool(PREF_SORT_FOLDERS_FIRST, true);
            prefsLoaded = true;
        }

        public static void SavePreferences()
        {
            EditorPrefs.SetInt(PREF_MENU_STYLE, menuStyle);
            EditorPrefs.SetString(PREF_FOLD_CHARS, foldCharacters ?? "");
            EditorPrefs.SetBool(PREF_GROUP_BY_FOLDER, groupByAssetFolder);
            EditorPrefs.SetBool(PREF_SORT_FOLDERS_FIRST, sortFoldersFirst);
        }

        public static string[] GetPreferenceKeys()
        {
            return new[]
            {
                PREF_MENU_STYLE,
                PREF_FOLD_CHARS,
                PREF_GROUP_BY_FOLDER,
                PREF_SORT_FOLDERS_FIRST
            };
        }

        /// <summary>
        /// Shows what the current settings would do to a clip name. The settings window lives in
        /// the Editor assembly and cannot reach ClipMenuFolder's internals, so this is the public
        /// entry point for the live preview. The asset folder is stubbed, since a preview string
        /// has no backing asset.
        /// </summary>
        public static string PreviewFold(string clipName)
        {
            if (string.IsNullOrEmpty(clipName)) return clipName;

            string separators = foldCharacters ?? "";
            string text = clipName;

            for (int i = 0; i < separators.Length; i++)
            {
                char sep = separators[i];
                if (sep == '/') continue;
                text = text.Replace(sep, '/');
            }

            if (groupByAssetFolder) text = "<clip's folder>/" + text;

            text = ClipMenuFolder.Tidy(text);
            return string.IsNullOrEmpty(text) ? clipName : text;
        }

        #endregion

        public static MethodBase TargetMethod()
        {
            Type popupType = AccessTools.TypeByName("UnityEditor.AnimationWindowClipPopup");
            if (popupType == null)
            {
                Warn("could not find UnityEditor.AnimationWindowClipPopup.");
                return null;
            }

            // private void DisplayClipMenu(Rect position, int controlID, AnimationClip clip)
            // Chosen because it does NOT touch AnimationWindowStyles - see the class comment.
            MethodInfo displayClipMenu = AccessTools.Method(popupType, "DisplayClipMenu",
                new[] { typeof(Rect), typeof(int), typeof(AnimationClip) });

            if (displayClipMenu == null)
            {
                Warn("could not find AnimationWindowClipPopup.DisplayClipMenu(Rect, int, AnimationClip).");
                return null;
            }

            if (!prefsLoaded) LoadPreferences();
            ClipMenuFolder.Install(popupType);
            return displayClipMenu;
        }

        [HarmonyPrefix]
        public static void Prefix(object __instance)
        {
            ClipMenuFolder.BeginClipMenu(__instance);
        }

        [HarmonyPostfix]
        public static void Postfix()
        {
            ClipMenuFolder.EndClipMenu();
        }

        internal static void Warn(string message)
        {
            Debug.LogWarning($"<color=#00FF00>[SDK at Home]</color> Foldered Animation Clip Dropdown: {message}");
        }
    }

    /// <summary>
    /// Folds clip labels into '/'-separated paths, but only while the Animation window's clip
    /// popup is the thing asking for a menu.
    /// </summary>
    internal static class ClipMenuFolder
    {
        private const string HarmonyId = "com.tohruthedragon.sdkathome.folderedclipdropdown";

        private static bool _installed;
        private static MethodInfo _getOrderedClipList;

        /// <summary>Set only for the duration of AnimationWindowClipPopup.DisplayClipMenu.</summary>
        private static bool _inClipMenu;

        /// <summary>The clips backing the current menu, used by the asset-folder option. Null if we
        /// could not obtain them, in which case that option degrades to leaving names alone.</summary>
        private static AnimationClip[] _currentClips;

        /// <summary>Held so the dropdown window is not collected while it is open.</summary>
        private static ClipAdvancedDropdown _activeDropdown;

        #region Install

        internal static void Install(Type popupType)
        {
            if (_installed) return;
            _installed = true;

            try
            {
                // Optional: lets the asset-folder option resolve each row back to its clip asset.
                // Safe to prepare - GetOrderedClipList has no AnimationWindowStyles reference.
                _getOrderedClipList = AccessTools.Method(popupType, "GetOrderedClipList", Type.EmptyTypes);

                MethodInfo displayCustomMenu = AccessTools.Method(typeof(EditorUtility), "DisplayCustomMenu",
                    new[]
                    {
                        typeof(Rect), typeof(GUIContent[]), typeof(Func<int, bool>), typeof(int),
                        typeof(EditorUtility.SelectMenuItemFunction), typeof(object), typeof(bool)
                    });

                if (displayCustomMenu == null)
                {
                    FolderedClipDropdown.Warn("could not resolve EditorUtility.DisplayCustomMenu; dropdown left stock.");
                    return;
                }

                var harmony = new Harmony(HarmonyId);
                harmony.Patch(displayCustomMenu, new HarmonyMethod(
                    typeof(ClipMenuFolder).GetMethod(nameof(DisplayCustomMenuPrefix),
                        BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception e)
            {
                FolderedClipDropdown.Warn($"install failed, dropdown left stock: {e.Message}");
            }
        }

        #endregion

        #region Scope

        internal static void BeginClipMenu(object popupInstance)
        {
            _inClipMenu = true;
            _currentClips = null;

            if (_getOrderedClipList == null || popupInstance == null) return;

            try
            {
                _currentClips = _getOrderedClipList.Invoke(popupInstance, null) as AnimationClip[];
            }
            catch (Exception)
            {
                _currentClips = null;
            }
        }

        internal static void EndClipMenu()
        {
            _inClipMenu = false;
            // _currentClips is deliberately kept: the AdvancedDropdown resolves selections after
            // DisplayClipMenu has already returned.
        }

        #endregion

        #region Menu interception

        /// <summary>
        /// Runs for every DisplayCustomMenu call in the editor, but does nothing unless we are
        /// inside the Animation window's clip popup. Returns false only when the AdvancedDropdown
        /// replaces the native menu entirely.
        /// </summary>
        private static bool DisplayCustomMenuPrefix(
            Rect position,
            ref GUIContent[] options,
            int selected,
            EditorUtility.SelectMenuItemFunction callback,
            object userData)
        {
            if (!_inClipMenu || options == null || options.Length == 0) return true;

            if (!FolderedClipDropdown.prefsLoaded) FolderedClipDropdown.LoadPreferences();

            GUIContent[] folded = Fold(options);

            if (FolderedClipDropdown.menuStyle == FolderedClipDropdown.MenuStyleAdvanced)
            {
                ShowAdvancedDropdown(position, folded, selected, callback, userData);
                return false; // we rendered the menu ourselves
            }

            if (FolderedClipDropdown.menuStyle == FolderedClipDropdown.MenuStyleSearchable)
            {
                ShowSearchableDropdown(position, folded, selected, callback, userData);
                return false;
            }

            options = folded;
            return true;
        }

        private static void ShowAdvancedDropdown(
            Rect position, GUIContent[] options, int selected,
            EditorUtility.SelectMenuItemFunction callback, object userData)
        {
            try
            {
                _activeDropdown = new ClipAdvancedDropdown(new AdvancedDropdownState());
                _activeDropdown.Initialize(options, CountClipRows(options), selected, callback, userData);
                _activeDropdown.Show(position);
            }
            catch (Exception e)
            {
                FolderedClipDropdown.Warn($"advanced dropdown failed, falling back to the native menu: {e.Message}");
                _activeDropdown = null;
                EditorUtility.DisplayCustomMenu(position, options, null, selected, callback, userData);
            }
        }

        internal static void ClearActiveDropdown()
        {
            _activeDropdown = null;
        }

        /// <summary>
        /// Shows SDK at Home's own searchable tree dropdown, the same widget the Parameter Driver
        /// patch uses. That window works on unique '/'-separated paths and reports the index it was
        /// given, so duplicate clip names get a numeric suffix and we keep a payload array mapping
        /// each row back to its original option index.
        /// </summary>
        private static void ShowSearchableDropdown(
            Rect position, GUIContent[] options, int selected,
            EditorUtility.SelectMenuItemFunction callback, object userData)
        {
            try
            {
                int clipCount = CountClipRows(options);

                var paths = new List<string>(options.Length);
                var optionIndices = new List<int>(options.Length);
                var used = new HashSet<string>();
                string current = "";

                for (int i = 0; i < clipCount && i < options.Length; i++)
                {
                    GUIContent option = options[i];
                    if (option == null || string.IsNullOrEmpty(option.text)) continue;

                    string path = MakeUnique(option.text, used);
                    if (i == selected) current = path;

                    paths.Add(path);
                    optionIndices.Add(i);
                }

                // Carry through Unity's trailing "Create New Clip..." row, unfolded.
                if (options.Length > clipCount)
                {
                    int lastIndex = options.Length - 1;
                    GUIContent last = options[lastIndex];
                    if (last != null && !string.IsNullOrEmpty(last.text))
                    {
                        paths.Add(MakeUnique(last.text, used));
                        optionIndices.Add(lastIndex);
                    }
                }

                if (paths.Count == 0) return;

                string[] pathArray = paths.ToArray();
                int[] indexArray = optionIndices.ToArray();
                GUIContent[] captured = options;

                SDKatHomeDropdownWindow.Show(position, pathArray, current, row =>
                {
                    if (row < 0 || row >= indexArray.Length) return;
                    InvokeMenuCallback(callback, userData, captured, indexArray[row]);
                });
            }
            catch (Exception e)
            {
                FolderedClipDropdown.Warn($"searchable dropdown failed, falling back to the native menu: {e.Message}");
                EditorUtility.DisplayCustomMenu(position, options, null, selected, callback, userData);
            }
        }

        /// <summary>Two clips can share a display name; the shared dropdown keys on the path, so
        /// collisions get a numeric suffix rather than silently selecting the wrong clip.</summary>
        private static string MakeUnique(string path, HashSet<string> used)
        {
            if (used.Add(path)) return path;

            for (int n = 2; ; n++)
            {
                string candidate = path + " (" + n + ")";
                if (used.Add(candidate)) return candidate;
            }
        }

        internal static void InvokeMenuCallback(
            EditorUtility.SelectMenuItemFunction callback, object userData,
            GUIContent[] options, int index)
        {
            if (callback == null) return;

            var texts = new string[options.Length];
            for (int i = 0; i < options.Length; i++)
                texts[i] = options[i] != null ? options[i].text : string.Empty;

            try
            {
                callback(userData, texts, index);
            }
            catch (Exception e)
            {
                FolderedClipDropdown.Warn($"selecting a clip failed: {e.Message}");
            }
        }

        #endregion

        #region Folding

        private static GUIContent[] Fold(GUIContent[] options)
        {
            string separators = FolderedClipDropdown.foldCharacters ?? "";
            bool byFolder = FolderedClipDropdown.groupByAssetFolder;

            if (separators.Length == 0 && !byFolder) return options;

            int clipCount = CountClipRows(options);

            // Fresh array, and fresh GUIContent only where we actually change something. Unity's
            // shared statics are copied by reference and never written to.
            var folded = new GUIContent[options.Length];
            Array.Copy(options, folded, options.Length);

            for (int i = 0; i < clipCount; i++)
            {
                GUIContent source = options[i];
                if (source == null || string.IsNullOrEmpty(source.text)) continue;

                string text = source.text;

                for (int c = 0; c < separators.Length; c++)
                {
                    char sep = separators[c];
                    if (sep == '/') continue; // already a separator; replacing would be a no-op
                    text = text.Replace(sep, '/');
                }

                if (byFolder) text = PrefixWithAssetFolder(i, text);

                text = Tidy(text);

                // A name made only of separators would collapse to nothing and vanish from the
                // menu, so keep the original in that case.
                if (!string.IsNullOrEmpty(text) && text != source.text)
                    folded[i] = new GUIContent(text, source.image, source.tooltip);
            }

            return folded;
        }

        /// <summary>
        /// How many leading entries are real clips. Unity appends GUIContent.none followed by
        /// "Create New Clip..." when the selection can create clips; the empty-text entry is the
        /// reliable marker for that pair. Falls back to the clip list when we have it.
        /// </summary>
        internal static int CountClipRows(GUIContent[] options)
        {
            if (_currentClips != null && _currentClips.Length <= options.Length)
                return _currentClips.Length;

            if (options.Length >= 2)
            {
                GUIContent separator = options[options.Length - 2];
                if (separator != null && string.IsNullOrEmpty(separator.text))
                    return options.Length - 2;
            }

            return options.Length;
        }

        /// <summary>Groups a clip under its immediate parent folder. One level only - the full
        /// project path would nest every clip several levels deep under Assets/.</summary>
        private static string PrefixWithAssetFolder(int index, string name)
        {
            if (_currentClips == null || index >= _currentClips.Length) return name;

            AnimationClip clip = _currentClips[index];
            if (clip == null) return name;

            string path = AssetDatabase.GetAssetPath(clip);
            if (string.IsNullOrEmpty(path)) return name;

            string dir = System.IO.Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) return name;

            dir = dir.Replace('\\', '/');
            int lastSlash = dir.LastIndexOf('/');
            string folder = lastSlash >= 0 ? dir.Substring(lastSlash + 1) : dir;

            return string.IsNullOrEmpty(folder) ? name : folder + "/" + name;
        }

        /// <summary>
        /// Collapses runs of '/' and trims them from both ends. Without this, a clip named
        /// "Idle..001" would fold to "Idle//001" and Unity would render an unnamed empty submenu.
        /// </summary>
        internal static string Tidy(string value)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf('/') < 0) return value;

            var sb = new StringBuilder(value.Length);
            bool previousWasSeparator = true; // seeded true so leading separators are dropped

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '/')
                {
                    if (previousWasSeparator) continue;
                    previousWasSeparator = true;
                }
                else
                {
                    previousWasSeparator = false;
                }
                sb.Append(c);
            }

            while (sb.Length > 0 && sb[sb.Length - 1] == '/')
                sb.Length--;

            return sb.ToString();
        }

        #endregion
    }

    /// <summary>
    /// Unity's searchable AdvancedDropdown, driven by the same folded labels the native menu uses.
    /// Item ids map back to the ORIGINAL option index, which is what the Animation window's
    /// callback expects - it uses the index to tell "pick clip N" from "create a new clip".
    /// </summary>
    internal class ClipAdvancedDropdown : AdvancedDropdown
    {
        private GUIContent[] _options;
        private int _clipCount;
        private int _selected;
        private EditorUtility.SelectMenuItemFunction _callback;
        private object _userData;

        private readonly Dictionary<int, int> _idToOptionIndex = new Dictionary<int, int>();
        private int _nextId = 1;

        public ClipAdvancedDropdown(AdvancedDropdownState state) : base(state)
        {
            minimumSize = new Vector2(280, 360);
        }

        public void Initialize(GUIContent[] options, int clipCount, int selected,
            EditorUtility.SelectMenuItemFunction callback, object userData)
        {
            _options = options;
            _clipCount = clipCount;
            _selected = selected;
            _callback = callback;
            _userData = userData;
            _idToOptionIndex.Clear();
            _nextId = 1;
        }

        protected override AdvancedDropdownItem BuildRoot()
        {
            var root = new AdvancedDropdownItem("Animation Clips");
            var folders = new Dictionary<string, AdvancedDropdownItem>();
            var topLevelFolders = new List<AdvancedDropdownItem>();
            var loose = new List<AdvancedDropdownItem>();

            for (int i = 0; i < _clipCount && i < _options.Length; i++)
            {
                GUIContent option = _options[i];
                if (option == null || string.IsNullOrEmpty(option.text)) continue;

                string[] parts = option.text.Split('/');
                string leaf = parts[parts.Length - 1];

                var item = new AdvancedDropdownItem(leaf) { id = _nextId++ };
                _idToOptionIndex[item.id] = i;

                if (parts.Length == 1)
                {
                    loose.Add(item);
                    continue;
                }

                // Walk (or create) the folder chain, then drop the clip in. Top-level folders are
                // held back rather than parented immediately, so the caller can decide whether
                // folders or loose clips come first without ever reparenting an item.
                AdvancedDropdownItem parent = null;
                string path = "";
                for (int p = 0; p < parts.Length - 1; p++)
                {
                    path = path.Length == 0 ? parts[p] : path + "/" + parts[p];

                    AdvancedDropdownItem folder;
                    if (!folders.TryGetValue(path, out folder))
                    {
                        folder = new AdvancedDropdownItem(parts[p]);
                        folders[path] = folder;

                        if (parent == null) topLevelFolders.Add(folder);
                        else parent.AddChild(folder);
                    }
                    parent = folder;
                }
                parent.AddChild(item);
            }

            if (FolderedClipDropdown.sortFoldersFirst)
            {
                foreach (var folder in topLevelFolders) root.AddChild(folder);
                foreach (var item in loose) root.AddChild(item);
            }
            else
            {
                foreach (var item in loose) root.AddChild(item);
                foreach (var folder in topLevelFolders) root.AddChild(folder);
            }

            AppendCreateNewClip(root);
            return root;
        }

        /// <summary>Carries through Unity's trailing "Create New Clip..." entry, unfolded and
        /// still mapped to its original index so the callback takes the create branch.</summary>
        private void AppendCreateNewClip(AdvancedDropdownItem root)
        {
            if (_options.Length <= _clipCount) return;

            int lastIndex = _options.Length - 1;
            GUIContent last = _options[lastIndex];
            if (last == null || string.IsNullOrEmpty(last.text)) return;

            root.AddSeparator();
            var create = new AdvancedDropdownItem(last.text) { id = _nextId++ };
            _idToOptionIndex[create.id] = lastIndex;
            root.AddChild(create);
        }

        protected override void ItemSelected(AdvancedDropdownItem item)
        {
            ClipMenuFolder.ClearActiveDropdown();

            int index;
            if (item == null || !_idToOptionIndex.TryGetValue(item.id, out index)) return;

            ClipMenuFolder.InvokeMenuCallback(_callback, _userData, _options, index);
        }
    }
}
#endif
