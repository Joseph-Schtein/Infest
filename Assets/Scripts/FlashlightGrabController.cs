using UnityEngine;

/// <summary>
/// Keeps the flashlight attached to the controller hand via scene parenting.
/// The Flashlight GameObject should be a CHILD of the hand anchor (e.g. LeftHandAnchor).
/// Movement is handled by Unity's transform hierarchy — this script only applies
/// local rotation/position offsets so you can tune the angle from the Inspector.
/// </summary>
public class FlashlightGrabController : MonoBehaviour
{
    [Header("Rotation Offset")]
    [Tooltip("Local rotation offset from the controller (Euler degrees).")]
    public Vector3 rotationOffset = new Vector3(90f, 0f, 0f);

    [Header("Position Offset")]
    [Tooltip("Local-space offset from the controller anchor (meters).")]
    public Vector3 positionOffset = Vector3.zero;

    void Start()
    {
        // Apply the offsets once on startup
        ApplyOffsets();
        Debug.Log($"[FlashlightGrab] Ready. Parent={transform.parent?.name ?? "ROOT"}, localRot={transform.localEulerAngles}");
    }

    /// <summary>
    /// Call this if you change the offsets at runtime (e.g. from a settings menu).
    /// </summary>
    public void ApplyOffsets()
    {
        transform.localPosition = positionOffset;
        transform.localRotation = Quaternion.Euler(rotationOffset);
    }
}
