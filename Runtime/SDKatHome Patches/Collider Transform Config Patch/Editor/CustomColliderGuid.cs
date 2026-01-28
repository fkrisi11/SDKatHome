using UnityEngine;
using VRC.SDKBase;

[DisallowMultipleComponent]
[ExecuteAlways]
public class CustomColliderGuid : MonoBehaviour, IEditorOnly
{
    [HideInInspector]
    public string avatarId;
}