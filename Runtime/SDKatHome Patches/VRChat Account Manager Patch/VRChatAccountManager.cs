#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace SDKatHome.Patches
{
    /// <summary>
    /// Adds a "Saved Accounts" section underneath the VRChat SDK panel's login box.
    ///
    /// Accounts are kept in <see cref="AccountVault"/> - encrypted against a device fingerprint and
    /// stored in EditorPrefs, so the same list shows up in every Unity project that has SDK at Home
    /// installed, and nothing lands in the project folder or in version control.
    ///
    /// Clicking a saved account fills the SDK's own Username/Email and Password fields and then
    /// invokes the SDK's own SignIn() - the actual authentication, 2FA prompt and credential
    /// handling all stay inside the SDK. This patch never touches the network.
    ///
    /// Implemented as a Postfix on VRCSdkControlPanel.ShowAccount() rather than on the inner
    /// AccountWindowGUI(): the login box is drawn inside a centering HorizontalScope, so postfixing
    /// the inner method would put our section beside the box instead of below it.
    /// See <see cref="SdkAccountPanelBridge.FindAccountGuiMethod"/>.
    /// </summary>
    public class VRChatAccountManager : SDKPatchBase
    {
        public override string PatchName => "VRChat Account Manager";

        public override string Description =>
            "Saves your VRChat SDK logins encrypted against this device and shows them under the " +
            "SDK panel's login box.";

        public override string Category => "SDK Panel";

        public override bool UsePrefix => false;
        public override bool UsePostfix => true;
        public override bool EnabledByDefault => true;

        public override string ButtonText => "Manage Accounts";
        public override string ButtonActionMethodName => "VRChatAccountManagerWindow.ShowWindow";

        private const string PREF_SORT_RECENT = "SDKatHome_VRChatAccounts_SortByRecent";

        /// <summary>
        /// Only the non-secret UI preference is reported here. The vault blob and its salt are
        /// deliberately left out so SDK at Home's "Clear Preferences" cannot wipe saved logins;
        /// that is what the manager window's explicit "Delete All Saved Accounts" is for.
        /// </summary>
        public static string[] GetPreferenceKeys()
        {
            return new[] { PREF_SORT_RECENT };
        }

        public static bool SortByMostRecent
        {
            get => EditorPrefs.GetBool(PREF_SORT_RECENT, true);
            set => EditorPrefs.SetBool(PREF_SORT_RECENT, value);
        }

        public static MethodBase TargetMethod()
        {
            return SdkAccountPanelBridge.FindAccountGuiMethod();
        }

        public static void Postfix(object __instance)
        {
            try
            {
                DrawSavedAccountsSection(__instance);
            }
            catch (Exception ex)
            {
                // Never let a drawing bug take down the SDK panel's Account tab.
                Debug.LogError($"<color=#00FF00>[SDK at Home]</color> VRChat Account Manager: {ex.Message}\n{ex.StackTrace}");
            }
        }

        #region GUI

        private const float PanelWidth = 340f;

        private static void DrawSavedAccountsSection(object panel)
        {
            if (!SdkAccountPanelBridge.IsUsable)
                return;

            // Nothing to offer while already signed in - and once the login has gone through, the
            // SDK is still holding the credentials in static strings it never clears itself.
            if (SdkAccountPanelBridge.IsLoggedIn())
            {
                SdkAccountPanelBridge.ClearTypedCredentials();
                return;
            }

            // Stay out of the way of the 2FA prompt that replaces the login box mid-sign-in. The
            // SDK's verification path still reads username/password, so nothing may be cleared here.
            if (SdkAccountPanelBridge.IsBusy())
                return;

            var accounts = AccountVault.LoadInfo();

            SavedAccountInfo signInWith = null;
            SavedAccountInfo fillWith = null;
            bool saveCurrent = false;
            bool openManager = false;

            EditorGUILayout.Space();

            using (new GUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.Width(PanelWidth)))
                {
                    EditorGUILayout.LabelField("Saved Accounts", EditorStyles.boldLabel);

                    if (AccountVault.IsUnreadable)
                    {
                        EditorGUILayout.HelpBox(
                            "Saved accounts could not be decrypted on this device.\n" + AccountVault.LastError,
                            MessageType.Warning);
                    }
                    else if (accounts.Count == 0)
                    {
                        EditorGUILayout.LabelField(
                            "No saved accounts yet. Type your login above, then press Save Current Login.",
                            WrappedMiniLabel);
                    }
                    else
                    {
                        foreach (var account in Ordered(accounts))
                        {
                            using (new GUILayout.HorizontalScope())
                            {
                                var content = new GUIContent(account.DisplayName, $"Sign in as {account.username}");

                                if (GUILayout.Button(content, GUILayout.Height(20)))
                                    signInWith = account;

                                if (GUILayout.Button(new GUIContent("Fill", "Fill the fields above without signing in"),
                                        EditorStyles.miniButton, GUILayout.Width(38), GUILayout.Height(20)))
                                    fillWith = account;
                            }
                        }
                    }

                    EditorGUILayout.Space(2);

                    using (new GUILayout.HorizontalScope())
                    {
                        string typedUser = SdkAccountPanelBridge.GetTypedUsername();
                        bool canSave = !string.IsNullOrEmpty(typedUser) && SdkAccountPanelBridge.HasTypedPassword();

                        using (new EditorGUI.DisabledScope(!canSave))
                        {
                            var saveContent = new GUIContent("Save Current Login",
                                canSave
                                    ? $"Save '{typedUser}' to this device's encrypted account list"
                                    : "Enter a username and password above first");

                            if (GUILayout.Button(saveContent, EditorStyles.miniButton, GUILayout.Height(20)))
                                saveCurrent = true;
                        }

                        if (GUILayout.Button(new GUIContent("Manage...", "Open the SDK at Home account manager"),
                                EditorStyles.miniButton, GUILayout.Width(70), GUILayout.Height(20)))
                            openManager = true;
                    }
                }
                GUILayout.FlexibleSpace();
            }

            // Act only after the layout is finished, so nothing mutates the list mid-iteration.
            // Each password is decrypted here, used, and wiped - it is never held between frames.
            if (fillWith != null)
            {
                byte[] password = AccountVault.TakePassword(fillWith.id);
                try { SdkAccountPanelBridge.Fill(fillWith.username, password); }
                finally { AccountVault.Zero(password); }
            }

            if (signInWith != null)
            {
                byte[] password = AccountVault.TakePassword(signInWith.id);
                try
                {
                    if (SdkAccountPanelBridge.SignIn(panel, signInWith.username, password))
                        AccountVault.MarkUsed(signInWith.id);
                }
                finally
                {
                    AccountVault.Zero(password);
                }
            }

            // Both of these put up a modal dialog / open a window; deferring keeps them out of the
            // SDK panel's IMGUI pass. The credentials they read live in static fields, so they are
            // still there next tick.
            if (saveCurrent)
                EditorApplication.delayCall += SaveCurrentLogin;

            if (openManager)
                EditorApplication.delayCall += OpenManagerWindow;
        }

        private static IEnumerable<SavedAccountInfo> Ordered(List<SavedAccountInfo> accounts)
        {
            if (!SortByMostRecent)
                return accounts;

            // Never-used entries sort last; ISO-8601 ("o") strings compare correctly as text.
            return accounts.OrderByDescending(a => a?.lastUsedUtc ?? "", StringComparer.Ordinal);
        }

        private static GUIStyle _wrappedMiniLabel;
        private static GUIStyle WrappedMiniLabel
        {
            get
            {
                if (_wrappedMiniLabel == null)
                    _wrappedMiniLabel = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
                return _wrappedMiniLabel;
            }
        }

        #endregion

        #region Actions

        private static void SaveCurrentLogin()
        {
            string username = SdkAccountPanelBridge.GetTypedUsername();
            if (string.IsNullOrEmpty(username) || !SdkAccountPanelBridge.HasTypedPassword())
                return;

            if (AccountVault.IsUnreadable &&
                !EditorUtility.DisplayDialog("Saved Accounts Unreadable",
                    "The existing saved accounts could not be decrypted on this device, so saving now " +
                    "will replace them with a new list containing only this account.\n\nContinue?",
                    "Replace", "Cancel"))
            {
                return;
            }

            if (AccountVault.FindByUsername(username) != null &&
                !EditorUtility.DisplayDialog("Account Already Saved",
                    $"'{username}' is already saved. Update its stored password?",
                    "Update", "Cancel"))
            {
                return;
            }

            // Pull the password out only once the user has confirmed, and wipe it straight after.
            byte[] password = SdkAccountPanelBridge.TakeTypedPasswordBytes();
            try
            {
                if (password != null)
                    AccountVault.AddOrUpdate(null, username, password);
            }
            finally
            {
                AccountVault.Zero(password);
            }
        }

        /// <summary>
        /// Opens the manager window by name. The window lives in the SDKatHome.Editor assembly,
        /// which this one cannot reference (it is the other way round), so it is resolved the same
        /// way SDKPatchInitializer resolves patch button actions.
        /// </summary>
        public static void OpenManagerWindow()
        {
            try
            {
                var windowType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(SafeGetTypes)
                    .FirstOrDefault(t => t.Name == "VRChatAccountManagerWindow");

                var show = windowType?.GetMethod("ShowWindow", BindingFlags.Public | BindingFlags.Static);
                if (show != null)
                    show.Invoke(null, null);
                else
                    Debug.LogError("<color=#00FF00>[SDK at Home]</color> VRChat Account Manager: could not find VRChatAccountManagerWindow.ShowWindow.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"<color=#00FF00>[SDK at Home]</color> VRChat Account Manager: could not open manager window - {ex.Message}");
            }
        }

        private static Type[] SafeGetTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null).ToArray(); }
            catch { return Type.EmptyTypes; }
        }

        #endregion
    }
}
#endif
