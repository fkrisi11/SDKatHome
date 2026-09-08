#if UNITY_EDITOR
using System;
using System.Reflection;
using HarmonyLib;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SDKatHome.Patches
{
    /// <summary>
    /// Adds Copy / Paste entries to the right-click menu of a state behaviour in the Inspector.
    ///
    /// Where we hook:
    ///   GenericMenu.ObjectContextDropDown(Rect, Object[] context, int) is what the Inspector calls
    ///   to show a component's context menu. It reads m_MenuItems into flat arrays and hands them
    ///   to a native popup, so a postfix would be too late and there is nothing to modify after the
    ///   fact. A prefix runs while the menu is still a GenericMenu, so items appended there show up
    ///   alongside Unity's own.
    ///
    ///   The prefix fires for EVERY object context menu in the editor, so it returns immediately
    ///   unless the context is a StateMachineBehaviour or a behaviour owner.
    ///
    /// Behaviours live on AnimatorState and on AnimatorStateMachine, and both are handled - layer
    /// and sub-state-machine behaviours are common enough to be worth supporting.
    ///
    /// Two kinds of paste:
    ///   "Paste as New"    adds a copy as an additional behaviour.
    ///   "Paste Values"    overwrites an existing behaviour of the same type in place, via
    ///                     EditorUtility.CopySerialized. Doing it in place rather than destroying
    ///                     and recreating the sub-asset keeps the object's identity, so anything
    ///                     referencing it stays valid and the asset file does not churn. The
    ///                     behaviour's own name and hideFlags are preserved, since pasting VALUES
    ///                     should not rename the sub-asset.
    /// </summary>
    [HarmonyPatch]
    public class StateBehaviourCopyPaste : SDKPatchBase
    {
        public override string PatchName => "State Behaviour Copy/Paste";

        public override string Description => "Adds copy and paste options to state behaviours";

        public override string Category => "Animator Tools";

        public override bool UsePrefix => true;
        public override bool UsePostfix => false;
        public override bool EnabledByDefault => true;

        public static MethodBase TargetMethod()
        {
            // internal void GenericMenu.ObjectContextDropDown(Rect position, Object[] context, int contextUserData)
            MethodInfo method = AccessTools.Method(typeof(GenericMenu), "ObjectContextDropDown",
                new[] { typeof(Rect), typeof(Object[]), typeof(int) });

            if (method == null)
            {
                Warn("could not find GenericMenu.ObjectContextDropDown(Rect, Object[], int).");
                return null;
            }

            BehaviourClipboard.InstallInspectorButton();
            return method;
        }

        [HarmonyPrefix]
        public static void Prefix(GenericMenu __instance, Object[] context)
        {
            if (__instance == null || context == null || context.Length != 1) return;

            Object target = context[0];
            if (target == null) return;

            var behaviour = target as StateMachineBehaviour;
            if (behaviour != null)
            {
                BehaviourClipboard.AddBehaviourItems(__instance, behaviour);
                return;
            }

            // Right-clicking the state (or state machine) itself only offers a paste.
            if (BehaviourClipboard.GetBehaviours(target) != null)
                BehaviourClipboard.AddOwnerItems(__instance, target);
        }

        internal static void Warn(string message)
        {
            Debug.LogWarning($"<color=#00FF00>[SDK at Home]</color> State Behaviour Copy/Paste: {message}");
        }
    }

    /// <summary>
    /// Holds the copied behaviour and performs the paste operations.
    /// </summary>
    internal static class BehaviourClipboard
    {
        private const string HarmonyId = "com.tohruthedragon.sdkathome.statebehaviourcopypaste";

        /// <summary>A detached clone of the copied behaviour. Not part of any asset.</summary>
        private static StateMachineBehaviour _clipboard;

        private static bool _buttonInstalled;
        private static PropertyInfo _stateProperty;
        private static PropertyInfo _stateMachineProperty;

        // Cached so the per-frame active check is a plain dictionary hit; GetPatchInfo(Type)
        // allocates via Activator.CreateInstance on every call.
        private static readonly string PatchName = new StateBehaviourCopyPaste().PatchName;

        #region Inspector button

        /// <summary>
        /// Adds a Paste button under Unity's "Add Behaviour" button.
        ///
        /// StateMachineBehaviorsEditor.AddBehaviourButton() is the last thing OnInspectorGUI does,
        /// so a postfix draws immediately below it - no layout maths, no guessing where the button
        /// ended up. The editor is internal, so the owner is read back through its public state /
        /// stateMachine properties by reflection.
        /// </summary>
        internal static void InstallInspectorButton()
        {
            if (_buttonInstalled) return;
            _buttonInstalled = true;

            try
            {
                Type editorType = AccessTools.TypeByName(
                    "UnityEditor.Graphs.AnimationStateMachine.StateMachineBehaviorsEditor");

                if (editorType == null)
                {
                    StateBehaviourCopyPaste.Warn("could not find StateMachineBehaviorsEditor; " +
                                                 "the Paste button will not appear (the right-click menu still works).");
                    return;
                }

                MethodInfo addButton = AccessTools.Method(editorType, "AddBehaviourButton", Type.EmptyTypes);
                if (addButton == null)
                {
                    StateBehaviourCopyPaste.Warn("could not find StateMachineBehaviorsEditor.AddBehaviourButton().");
                    return;
                }

                _stateProperty = AccessTools.Property(editorType, "state");
                _stateMachineProperty = AccessTools.Property(editorType, "stateMachine");

                new Harmony(HarmonyId).Patch(addButton, null, new HarmonyMethod(
                    typeof(BehaviourClipboard).GetMethod(nameof(AddBehaviourButtonPostfix),
                        BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception e)
            {
                StateBehaviourCopyPaste.Warn($"could not add the Paste button: {e.Message}");
            }
        }

        private static void AddBehaviourButtonPostfix(object __instance)
        {
            if (!HasClipboard) return;

            // The framework only unpatches this patch's own registered target, so the button hook
            // stays installed when the patch is switched off. Check explicitly.
            if (!SDKatHomePatcher.IsPatchActive(PatchName)) return;

            Object owner = GetOwnerFromEditor(__instance);
            if (owner == null) return;

            DrawPasteButton(owner);
        }

        private static void DrawPasteButton(Object owner)
        {
            var label = new GUIContent("Paste Behaviour",
                "Paste the copied " + _clipboard.GetType().Name + " as a new behaviour.");

            // Mirrors Unity's own Add Behaviour button: LargeButton, 230 wide, centred.
            var style = (GUIStyle)"LargeButton";
            Rect rect = GUILayoutUtility.GetRect(label, style);
            rect.x += (rect.width - 230f) / 2f;
            rect.width = 230f;

            if (GUI.Button(rect, label, style))
            {
                PasteAsNew(owner);
                // No ExitGUI: the editor list rebuilds on the next OnInspectorGUI because
                // IsEditorsValid notices the behaviour count changed.
            }

            EditorGUILayout.Space();
        }

        private static Object GetOwnerFromEditor(object editorInstance)
        {
            if (editorInstance == null) return null;

            try
            {
                if (_stateProperty != null)
                {
                    var state = _stateProperty.GetValue(editorInstance, null) as Object;
                    if (state != null) return state;
                }

                if (_stateMachineProperty != null)
                {
                    var machine = _stateMachineProperty.GetValue(editorInstance, null) as Object;
                    if (machine != null) return machine;
                }
            }
            catch (Exception)
            {
                // A malformed editor instance is not worth logging every repaint.
            }

            return null;
        }

        #endregion

        #region Menu

        internal static void AddBehaviourItems(GenericMenu menu, StateMachineBehaviour behaviour)
        {
            // The behaviour has to actually belong to something in the current selection, otherwise
            // we have no owner to paste into and no business adding entries.
            Object owner = FindOwner(behaviour);
            if (owner == null) return;

            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Copy Behaviour"), false, () => Copy(behaviour));

            AddItem(menu, "Paste Behaviour as New", HasClipboard,
                () => PasteAsNew(owner));

            AddItem(menu, "Paste Behaviour Values",
                HasClipboard && _clipboard.GetType() == behaviour.GetType(),
                () => PasteValues(behaviour));
        }

        internal static void AddOwnerItems(GenericMenu menu, Object owner)
        {
            if (!HasClipboard) return;

            menu.AddSeparator("");
            AddItem(menu, "Paste Behaviour as New", true, () => PasteAsNew(owner));
        }

        private static void AddItem(GenericMenu menu, string label, bool enabled, GenericMenu.MenuFunction action)
        {
            var content = new GUIContent(label);
            if (enabled) menu.AddItem(content, false, action);
            else menu.AddDisabledItem(content);
        }

        private static bool HasClipboard => _clipboard != null;

        #endregion

        #region Operations

        private static void Copy(StateMachineBehaviour source)
        {
            if (source == null) return;

            // Replace rather than accumulate: the previous clone is ours and nothing else
            // references it, so leaving it around would just leak a ScriptableObject per copy.
            if (_clipboard != null) Object.DestroyImmediate(_clipboard);

            _clipboard = Object.Instantiate(source);
            _clipboard.name = source.name;
            _clipboard.hideFlags = HideFlags.HideAndDontSave;
        }

        private static void PasteAsNew(Object owner)
        {
            if (_clipboard == null || owner == null) return;

            // Let Unity create it. AddStateMachineBehaviour is a native call that allocates the
            // object, registers it as a sub-asset of the controller and appends it to the
            // behaviours array in one step. Doing that by hand - Instantiate, ArrayUtility.Add,
            // AddObjectToAsset - is where orphaned sub-assets and undo trouble come from.
            StateMachineBehaviour created = AddBehaviour(owner, _clipboard.GetType());
            if (created == null) return;

            // Then copy the values over the fresh instance. Keep the name and flags Unity gave it,
            // so a pasted behaviour is indistinguishable from one added via Add Behaviour.
            string name = created.name;
            HideFlags flags = created.hideFlags;

            EditorUtility.CopySerialized(_clipboard, created);

            created.name = name;
            created.hideFlags = flags;

            EditorUtility.SetDirty(owner);
        }

        private static void PasteValues(StateMachineBehaviour target)
        {
            if (_clipboard == null || target == null) return;
            if (_clipboard.GetType() != target.GetType()) return;

            Undo.RecordObject(target, "Paste Behaviour Values");

            // CopySerialized overwrites every serialized field, including m_Name and
            // m_ObjectHideFlags. Pasting values should not rename or unhide the sub-asset.
            string name = target.name;
            HideFlags flags = target.hideFlags;

            EditorUtility.CopySerialized(_clipboard, target);

            target.name = name;
            target.hideFlags = flags;

            EditorUtility.SetDirty(target);
        }

        #endregion

        #region Owner lookup

        /// <summary>
        /// AnimatorState and AnimatorStateMachine both expose a behaviours array but share no
        /// interface, so the two cases are handled explicitly.
        /// </summary>
        internal static StateMachineBehaviour[] GetBehaviours(Object owner)
        {
            var state = owner as AnimatorState;
            if (state != null) return state.behaviours;

            var machine = owner as AnimatorStateMachine;
            if (machine != null) return machine.behaviours;

            return null;
        }

        private static StateMachineBehaviour AddBehaviour(Object owner, Type type)
        {
            var state = owner as AnimatorState;
            if (state != null) return state.AddStateMachineBehaviour(type);

            var machine = owner as AnimatorStateMachine;
            if (machine != null) return machine.AddStateMachineBehaviour(type);

            return null;
        }

        /// <summary>
        /// The context menu gives us the behaviour but not what it belongs to. The Inspector is
        /// showing the behaviour because its owner is selected, so the owner is in Selection.
        /// </summary>
        private static Object FindOwner(StateMachineBehaviour behaviour)
        {
            Object[] selection = Selection.objects;
            if (selection == null) return null;

            foreach (Object candidate in selection)
            {
                if (candidate == null) continue;

                StateMachineBehaviour[] behaviours = GetBehaviours(candidate);
                if (behaviours == null) continue;

                if (Array.IndexOf(behaviours, behaviour) >= 0) return candidate;
            }

            return null;
        }

        #endregion
    }
}
#endif
