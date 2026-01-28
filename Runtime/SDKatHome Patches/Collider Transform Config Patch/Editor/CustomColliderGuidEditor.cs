#if UNITY_EDITOR
using UnityEditor;

[CustomEditor(typeof(CustomColliderGuid))]
public class CustomColliderGuidEditor : Editor
{
    public override void OnInspectorGUI()
    {
        var guid = (CustomColliderGuid)target;

        EditorGUILayout.LabelField("Avatar Identity", EditorStyles.boldLabel);
        EditorGUILayout.Space(2);

        using (new EditorGUI.DisabledScope(true))
        {
            EditorGUILayout.TextField(
                "Avatar ID",
                string.IsNullOrEmpty(guid.avatarId) ? "<not assigned>" : guid.avatarId
            );
        }

        EditorGUILayout.HelpBox(
            "This ID uniquely identifies the avatar across prefabs, duplicates, and SDK operations.\n" +
            "It is generated automatically and should not be edited.",
            MessageType.Info
        );
    }
}
#endif