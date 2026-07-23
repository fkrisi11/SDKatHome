#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text;
using SDKatHome.Patches;
using UnityEditor;
using UnityEngine;

namespace SDKatHome
{
    /// <summary>
    /// Manager for the SDK at Home VRChat account vault.
    ///
    /// Stored passwords are never displayed or read back out - a stored password only leaves the
    /// vault to be written straight into the SDK panel's own password field. Changing one means
    /// typing a new password, not revealing the old one.
    ///
    /// The password fields below are the one place a plaintext string is unavoidable: Unity's
    /// EditorGUILayout.PasswordField has no byte[] form, and IMGUI needs the value to survive
    /// between frames, so it must live in a field. Those buffers are dropped the moment the value
    /// is committed to the vault, which converts it to bytes and wipes them after use.
    /// </summary>
    public class VRChatAccountManagerWindow : EditorWindow
    {
        private Vector2 _scroll;

        // New-account form
        private string _newLabel = "";
        private string _newUsername = "";
        private string _newPassword = "";

        // Per-account inline editors, keyed by account id.
        private readonly Dictionary<string, string> _passwordEdits = new Dictionary<string, string>();
        private readonly HashSet<string> _renaming = new HashSet<string>();
        private readonly Dictionary<string, string> _labelEdits = new Dictionary<string, string>();

        // Anything that mutates the vault or opens a modal dialog is queued here and run via
        // delayCall, outside the IMGUI pass - the same pattern the patch uses for its section
        // under the SDK panel. Doing it mid-OnGUI desynchronises IMGUI's Layout/Repaint passes.
        private bool _pendingAdd;
        private bool _pendingDeleteAll;
        private string _pendingDeleteId;
        private string _pendingPasswordChangeId;
        private SavedAccountInfo _pendingSignIn;

        public static void ShowWindow()
        {
            var window = GetWindow<VRChatAccountManagerWindow>("VRChat Accounts");
            window.minSize = new Vector2(420, 420);
            window.Show();
        }

        private void OnFocus()
        {
            // Another Unity instance shares the same EditorPrefs vault.
            AccountVault.InvalidateCache();
            Repaint();
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawHeader();
            EditorGUILayout.Space();

            DrawAddAccount();
            EditorGUILayout.Space();

            DrawAccountList();
            EditorGUILayout.Space();

            DrawDangerZone();

            EditorGUILayout.EndScrollView();

            if (HasPendingActions)
                EditorApplication.delayCall += ProcessPendingActions;
        }

        private bool HasPendingActions =>
            _pendingAdd || _pendingDeleteAll || _pendingDeleteId != null
            || _pendingPasswordChangeId != null || _pendingSignIn != null;

        private void ProcessPendingActions()
        {
            // The window may have been closed between OnGUI and the delayCall firing.
            if (this == null)
                return;

            if (!HasPendingActions)
                return;

            bool add = _pendingAdd;
            bool deleteAll = _pendingDeleteAll;
            string deleteId = _pendingDeleteId;
            string passwordChangeId = _pendingPasswordChangeId;
            var signIn = _pendingSignIn;

            _pendingAdd = false;
            _pendingDeleteAll = false;
            _pendingDeleteId = null;
            _pendingPasswordChangeId = null;
            _pendingSignIn = null;

            if (add)
                AddNewAccount();

            if (passwordChangeId != null)
                ApplyPasswordChange(passwordChangeId);

            if (deleteId != null)
            {
                string name = AccountVault.FindById(deleteId)?.username ?? "this account";

                if (EditorUtility.DisplayDialog("Delete Saved Account",
                        $"Delete the saved login for '{name}'?", "Delete", "Cancel"))
                {
                    AccountVault.Remove(deleteId);
                    _passwordEdits.Remove(deleteId);
                    _labelEdits.Remove(deleteId);
                    _renaming.Remove(deleteId);
                }
            }

            if (deleteAll &&
                EditorUtility.DisplayDialog("Delete All Saved Accounts",
                    "Permanently delete every VRChat login saved on this device?\n\n" +
                    "This affects all Unity projects on this machine and cannot be undone.",
                    "Delete All", "Cancel"))
            {
                AccountVault.DeleteAll();
                _passwordEdits.Clear();
                _labelEdits.Clear();
                _renaming.Clear();
            }

            if (signIn != null)
                SignInWith(signIn);

            Repaint();
        }

        #region Sections

        private void DrawHeader()
        {
            EditorGUILayout.LabelField("VRChat Account Manager", EditorStyles.boldLabel);

            if (AccountVault.IsUnreadable)
            {
                EditorGUILayout.HelpBox(
                    "The saved accounts on this machine could not be decrypted:\n" + AccountVault.LastError +
                    "\n\nNothing has been deleted. If this device changed, use Delete All Saved Accounts " +
                    "below and add your logins again.",
                    MessageType.Error);
            }

            if (!SdkAccountPanelBridge.IsUsable)
            {
                EditorGUILayout.HelpBox(
                    "The VRChat SDK control panel could not be found, so signing in from here is unavailable. " +
                    "Saved accounts can still be managed.",
                    MessageType.Warning);
            }

            EditorGUI.BeginChangeCheck();
            bool sortRecent = EditorGUILayout.ToggleLeft(
                new GUIContent("Sort by most recently used",
                    "Affects the list here and the one under the SDK panel's login box"),
                VRChatAccountManager.SortByMostRecent);
            if (EditorGUI.EndChangeCheck())
                VRChatAccountManager.SortByMostRecent = sortRecent;
        }

        private void DrawAddAccount()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Add Account", EditorStyles.boldLabel);

                _newLabel = EditorGUILayout.TextField(
                    new GUIContent("Label (optional)", "Shown in the account list. Defaults to the username."),
                    _newLabel);
                _newUsername = EditorGUILayout.TextField("Username/Email", _newUsername);
                _newPassword = EditorGUILayout.PasswordField("Password", _newPassword);

                bool canAdd = !string.IsNullOrEmpty(_newUsername) && !string.IsNullOrEmpty(_newPassword);

                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();

                    using (new EditorGUI.DisabledScope(!canAdd))
                    {
                        if (GUILayout.Button("Save Account", GUILayout.Width(120)))
                            _pendingAdd = true;
                    }

                    using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_newUsername) && string.IsNullOrEmpty(_newPassword) && string.IsNullOrEmpty(_newLabel)))
                    {
                        if (GUILayout.Button("Clear", GUILayout.Width(60)))
                            ClearNewAccountForm();
                    }
                }
            }
        }

        private void DrawAccountList()
        {
            var accounts = AccountVault.LoadInfo();

            EditorGUILayout.LabelField($"Saved Accounts ({accounts.Count})", EditorStyles.boldLabel);

            if (accounts.Count == 0)
            {
                EditorGUILayout.HelpBox("No accounts saved on this device yet.", MessageType.None);
                return;
            }

            foreach (var account in accounts)
            {
                if (account == null)
                    continue;

                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    DrawAccountRow(account);
                }
            }
        }

        private void DrawAccountRow(SavedAccountInfo account)
        {
            bool renaming = _renaming.Contains(account.id);

            using (new GUILayout.HorizontalScope())
            {
                if (renaming)
                {
                    if (!_labelEdits.TryGetValue(account.id, out string labelEdit))
                        labelEdit = account.DisplayName;

                    _labelEdits[account.id] = EditorGUILayout.TextField(labelEdit);

                    if (GUILayout.Button("Save", EditorStyles.miniButton, GUILayout.Width(45)))
                    {
                        AccountVault.SetLabel(account.id, _labelEdits[account.id]);
                        _renaming.Remove(account.id);
                        _labelEdits.Remove(account.id);
                    }

                    if (GUILayout.Button("Cancel", EditorStyles.miniButton, GUILayout.Width(55)))
                    {
                        _renaming.Remove(account.id);
                        _labelEdits.Remove(account.id);
                    }
                }
                else
                {
                    EditorGUILayout.LabelField(account.DisplayName, EditorStyles.boldLabel);

                    if (GUILayout.Button(new GUIContent("Rename", "Change the display label"),
                            EditorStyles.miniButton, GUILayout.Width(60)))
                    {
                        _renaming.Add(account.id);
                        _labelEdits[account.id] = account.DisplayName;
                    }

                    if (GUILayout.Button(new GUIContent("Delete", "Remove this account from this device"),
                            EditorStyles.miniButton, GUILayout.Width(55)))
                        _pendingDeleteId = account.id;
                }
            }

            EditorGUILayout.LabelField("Username/Email", account.username ?? "");
            EditorGUILayout.LabelField("Password", "********");
            EditorGUILayout.LabelField("Last used", FormatLastUsed(account.lastUsedUtc));

            using (new GUILayout.HorizontalScope())
            {
                bool editingPassword = _passwordEdits.ContainsKey(account.id);

                if (editingPassword)
                {
                    _passwordEdits[account.id] = EditorGUILayout.PasswordField("New Password", _passwordEdits[account.id]);

                    using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_passwordEdits[account.id])))
                    {
                        if (GUILayout.Button("Apply", EditorStyles.miniButton, GUILayout.Width(50)))
                            _pendingPasswordChangeId = account.id;
                    }

                    if (GUILayout.Button("Cancel", EditorStyles.miniButton, GUILayout.Width(55)))
                        _passwordEdits.Remove(account.id);
                }
                else
                {
                    if (GUILayout.Button(new GUIContent("Change Password", "Replace the stored password"),
                            EditorStyles.miniButton))
                        _passwordEdits[account.id] = "";

                    using (new EditorGUI.DisabledScope(!SdkAccountPanelBridge.IsUsable))
                    {
                        var content = new GUIContent("Sign In",
                            SdkAccountPanelBridge.IsUsable
                                ? "Fill this login into the SDK panel and sign in"
                                : "The VRChat SDK control panel was not found");

                        if (GUILayout.Button(content, EditorStyles.miniButton, GUILayout.Width(70)))
                            _pendingSignIn = account;
                    }
                }
            }
        }

        private void DrawDangerZone()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Danger Zone", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    "SDK at Home's \"Clear Preferences\" deliberately leaves saved accounts alone. " +
                    "This is the only way to remove them all.",
                    new GUIStyle(EditorStyles.miniLabel) { wordWrap = true });

                if (GUILayout.Button("Delete All Saved Accounts"))
                    _pendingDeleteAll = true;
            }
        }

        #endregion

        #region Actions

        private void AddNewAccount()
        {
            if (AccountVault.IsUnreadable &&
                !EditorUtility.DisplayDialog("Saved Accounts Unreadable",
                    "The existing saved accounts could not be decrypted on this device, so saving now " +
                    "will replace them with a new list containing only this account.\n\nContinue?",
                    "Replace", "Cancel"))
            {
                return;
            }

            if (AccountVault.FindByUsername(_newUsername) != null &&
                !EditorUtility.DisplayDialog("Account Already Saved",
                    $"'{_newUsername}' is already saved. Update its stored password?",
                    "Update", "Cancel"))
            {
                return;
            }

            byte[] password = Encoding.UTF8.GetBytes(_newPassword);
            try
            {
                AccountVault.AddOrUpdate(_newLabel, _newUsername, password);
            }
            finally
            {
                AccountVault.Zero(password);
            }

            ClearNewAccountForm();
            GUI.FocusControl(null);
        }

        private void ApplyPasswordChange(string id)
        {
            if (!_passwordEdits.TryGetValue(id, out string typed) || string.IsNullOrEmpty(typed))
                return;

            byte[] password = Encoding.UTF8.GetBytes(typed);
            try
            {
                AccountVault.SetPassword(id, password);
            }
            finally
            {
                AccountVault.Zero(password);
            }

            // Drop the string buffer so the typed password is not held past the commit.
            _passwordEdits.Remove(id);
            GUI.FocusControl(null);
        }

        private void ClearNewAccountForm()
        {
            _newLabel = "";
            _newUsername = "";
            _newPassword = "";
        }

        private void SignInWith(SavedAccountInfo account)
        {
            var panel = SdkAccountPanelBridge.FindOpenPanel();
            if (panel == null)
            {
                EditorUtility.DisplayDialog("SDK Panel Not Open",
                    "Open the VRChat SDK control panel (VRChat SDK > Show Control Panel) first, then sign in.",
                    "OK");
                return;
            }

            panel.Focus();

            // Decrypted here, handed straight to the SDK, wiped immediately.
            byte[] password = AccountVault.TakePassword(account.id);
            try
            {
                if (SdkAccountPanelBridge.SignIn(panel, account.username, password))
                {
                    AccountVault.MarkUsed(account.id);
                    panel.Repaint();
                }
            }
            finally
            {
                AccountVault.Zero(password);
            }
        }

        private static string FormatLastUsed(string isoUtc)
        {
            if (string.IsNullOrEmpty(isoUtc))
                return "Never";

            return System.DateTime.TryParse(isoUtc, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToLocalTime().ToString("g")
                : "Unknown";
        }

        #endregion
    }
}
#endif
