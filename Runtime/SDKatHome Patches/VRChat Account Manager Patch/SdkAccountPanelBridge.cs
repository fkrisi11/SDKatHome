#if UNITY_EDITOR
using System;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEditor;
using UnityEngine;

namespace SDKatHome.Patches
{
    /// <summary>
    /// Reflection bridge into VRCSdkControlPanel's Account tab.
    ///
    /// Everything here is looked up by name instead of referenced directly: VRCSdkControlPanel lives
    /// in VRC.SDKBase.Editor, which SDK at Home's asmdef does not reference, and the members we need
    /// (username / password / signingIn / SignIn) are all private. Lookups are cached and every
    /// accessor degrades to a no-op / safe default if the SDK changes shape, so a future SDK update
    /// can only make this patch stop working - it can never break the SDK panel itself.
    /// </summary>
    public static class SdkAccountPanelBridge
    {
        private static bool _resolved;

        private static Type _panelType;
        private static Type _apiUserType;
        private static PropertyInfo _usernameProp;
        private static PropertyInfo _passwordProp;
        private static PropertyInfo _isLoggedInProp;
        private static FieldInfo _signingInField;
        private static FieldInfo _twoFactorField;
        private static MethodInfo _signInMethod;

        public static Type PanelType { get { Resolve(); return _panelType; } }

        /// <summary>True when every member the patch relies on was found.</summary>
        public static bool IsUsable
        {
            get
            {
                Resolve();
                return _panelType != null
                    && _usernameProp != null
                    && _passwordProp != null
                    && _signInMethod != null;
            }
        }

        private static void Resolve()
        {
            if (_resolved)
                return;
            _resolved = true;

            _panelType = AccessTools.TypeByName("VRCSdkControlPanel");
            if (_panelType == null)
            {
                Debug.LogWarning("<color=#00FF00>[SDK at Home]</color> VRChat Account Manager: could not find VRCSdkControlPanel.");
                return;
            }

            // static string username { get; set; } / static string password { get; set; }
            _usernameProp = AccessTools.Property(_panelType, "username");
            _passwordProp = AccessTools.Property(_panelType, "password");

            // static bool signingIn / static TwoFactorType _twoFactorAuthenticationEntryType
            _signingInField = AccessTools.Field(_panelType, "signingIn");
            _twoFactorField = AccessTools.Field(_panelType, "_twoFactorAuthenticationEntryType");

            // private void SignIn(bool explicitAttempt)
            _signInMethod = AccessTools.Method(_panelType, "SignIn", new[] { typeof(bool) });

            _apiUserType = AccessTools.TypeByName("VRC.Core.APIUser");
            if (_apiUserType != null)
                _isLoggedInProp = AccessTools.Property(_apiUserType, "IsLoggedIn");
        }

        /// <summary>
        /// The method this patch postfixes, so our section lands underneath the login box.
        ///
        /// ShowAccount() is preferred over the inner OnAccountGUI(): it is the whole Account tab and
        /// calls OnAccountGUI() last, so a postfix draws below everything, and being a large method
        /// it will not be inlined into the IMGUIContainer lambda that calls it (which would make the
        /// patch silently do nothing). OnAccountGUI() is the fallback if the SDK renames ShowAccount.
        /// </summary>
        public static MethodBase FindAccountGuiMethod()
        {
            Resolve();
            if (_panelType == null)
                return null;

            var method = AccessTools.Method(_panelType, "ShowAccount")
                         ?? AccessTools.Method(_panelType, "OnAccountGUI");

            if (method == null)
                Debug.LogWarning("<color=#00FF00>[SDK at Home]</color> VRChat Account Manager: could not find VRCSdkControlPanel.ShowAccount / OnAccountGUI.");

            return method;
        }

        #region Panel state

        public static bool IsLoggedIn()
        {
            Resolve();
            if (_isLoggedInProp == null)
                return false;

            try { return (bool)_isLoggedInProp.GetValue(null, null); }
            catch { return false; }
        }

        /// <summary>True while a sign-in (including the 2FA prompt) is in flight.</summary>
        public static bool IsBusy()
        {
            Resolve();

            try
            {
                if (_signingInField != null && (bool)_signingInField.GetValue(null))
                    return true;

                // TwoFactorType.None == 0
                if (_twoFactorField != null && Convert.ToInt32(_twoFactorField.GetValue(null)) != 0)
                    return true;
            }
            catch
            {
                // If we cannot read the state, assume busy so we never draw over the 2FA prompt.
                return true;
            }

            return false;
        }

        public static string GetTypedUsername()
        {
            Resolve();
            if (_usernameProp == null) return null;
            try { return _usernameProp.GetValue(null, null) as string; }
            catch { return null; }
        }

        /// <summary>Whether a password has been typed, without pulling it out of the SDK.</summary>
        public static bool HasTypedPassword()
        {
            return !string.IsNullOrEmpty(ReadTypedPassword());
        }

        /// <summary>
        /// The typed password as bytes. The caller owns the array and must zero it.
        /// The SDK holds this as a string either way, so this does not shorten *its* copy's life -
        /// it just stops us from making a second long-lived one.
        /// </summary>
        public static byte[] TakeTypedPasswordBytes()
        {
            string typed = ReadTypedPassword();
            return string.IsNullOrEmpty(typed) ? null : Encoding.UTF8.GetBytes(typed);
        }

        private static string ReadTypedPassword()
        {
            Resolve();
            if (_passwordProp == null) return null;
            try { return _passwordProp.GetValue(null, null) as string; }
            catch { return null; }
        }

        #endregion

        #region Actions

        /// <summary>
        /// Writes the credentials into the panel's Username/Email and Password fields.
        ///
        /// This is the boundary where a plaintext password string is unavoidable: the SDK stores it
        /// in a static string property and passes it to APIUser.Login as a string, so one has to
        /// exist. We build it here, at the last possible moment, from the caller's byte[] - which
        /// the caller then zeroes. See <see cref="ClearTypedCredentials"/> for the other end.
        ///
        /// Keyboard focus is dropped first: an IMGUI text field that currently owns focus keeps its
        /// own edit buffer and would write the old text straight back over ours next repaint.
        /// </summary>
        public static bool Fill(string username, byte[] password)
        {
            Resolve();
            if (_usernameProp == null || _passwordProp == null)
                return false;

            try
            {
                GUIUtility.keyboardControl = 0;
                EditorGUIUtility.editingTextField = false;
                GUI.FocusControl(null);

                _usernameProp.SetValue(null, username, null);
                _passwordProp.SetValue(null, password == null ? "" : Encoding.UTF8.GetString(password), null);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"<color=#00FF00>[SDK at Home]</color> VRChat Account Manager: failed to fill login fields - {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Drops the credentials the SDK is holding in its static username/password fields.
        ///
        /// The SDK never clears these itself after a successful login, so without this the password
        /// would sit in a string for the rest of the editor session. Only safe once the user is
        /// actually logged in: the 2FA path still reads both fields while verification is pending.
        /// </summary>
        public static void ClearTypedCredentials()
        {
            Resolve();

            try
            {
                // Called every repaint while logged in, so skip the writes once it is already clear.
                if (_usernameProp?.GetValue(null, null) != null)
                    _usernameProp.SetValue(null, null, null);

                if (_passwordProp?.GetValue(null, null) != null)
                    _passwordProp.SetValue(null, null, null);
            }
            catch
            {
                // Best effort only - this is a hardening step, not something to fail loudly on.
            }
        }

        /// <summary>Fills the login fields and presses the panel's own Sign In for the given account.</summary>
        public static bool SignIn(object panelInstance, string username, byte[] password)
        {
            Resolve();
            if (_signInMethod == null || panelInstance == null)
                return false;

            if (!Fill(username, password))
                return false;

            try
            {
                _signInMethod.Invoke(panelInstance, new object[] { true });
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Debug.LogError($"<color=#00FF00>[SDK at Home]</color> VRChat Account Manager: sign in failed - {ex.InnerException?.Message ?? ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError($"<color=#00FF00>[SDK at Home]</color> VRChat Account Manager: sign in failed - {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Finds the open SDK panel window so the manager window can drive a sign-in.
        /// Returns null when the SDK panel is not open.
        /// </summary>
        public static EditorWindow FindOpenPanel()
        {
            Resolve();
            if (_panelType == null)
                return null;

            var open = Resources.FindObjectsOfTypeAll(_panelType);
            return open != null && open.Length > 0 ? open[0] as EditorWindow : null;
        }

        #endregion

        [UnityEditor.Callbacks.DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            _resolved = false;
            _panelType = null;
            _apiUserType = null;
            _usernameProp = null;
            _passwordProp = null;
            _isLoggedInProp = null;
            _signingInField = null;
            _twoFactorField = null;
            _signInMethod = null;
        }
    }
}
#endif
