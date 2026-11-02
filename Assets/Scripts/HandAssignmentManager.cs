using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Lets the player choose which hand holds the flashlight and which holds the spray can.
/// Provides a simple world-space menu at game start.
/// Other scripts read HandAssignment to know their controller mapping.
/// </summary>
public class HandAssignmentManager : MonoBehaviour
{
    public static HandAssignmentManager Instance { get; private set; }

    public enum HandLayout
    {
        FlashLeftSprayRight,
        FlashRightSprayLeft
    }

    [Header("Default Layout")]
    public HandLayout currentLayout = HandLayout.FlashLeftSprayRight;

    [Header("Events")]
    public UnityEvent<HandLayout> OnHandsAssigned;

    /// <summary>
    /// Returns the OVRInput.Controller for the flashlight hand.
    /// </summary>
    public OVRInput.Controller FlashlightController
    {
        get
        {
            return currentLayout == HandLayout.FlashLeftSprayRight
                ? OVRInput.Controller.LTouch
                : OVRInput.Controller.RTouch;
        }
    }

    /// <summary>
    /// Returns the OVRInput.Controller for the spray can hand.
    /// </summary>
    public OVRInput.Controller SprayController
    {
        get
        {
            return currentLayout == HandLayout.FlashLeftSprayRight
                ? OVRInput.Controller.RTouch
                : OVRInput.Controller.LTouch;
        }
    }

    /// <summary>
    /// Returns the anchor name for the flashlight hand.
    /// </summary>
    public string FlashlightAnchorName
    {
        get
        {
            return currentLayout == HandLayout.FlashLeftSprayRight
                ? "LeftHandAnchor"
                : "RightHandAnchor";
        }
    }

    /// <summary>
    /// Returns the anchor name for the spray can hand.
    /// </summary>
    public string SprayAnchorName
    {
        get
        {
            return currentLayout == HandLayout.FlashLeftSprayRight
                ? "RightHandAnchor"
                : "LeftHandAnchor";
        }
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    /// <summary>
    /// Call this from a UI button to set layout option 1: Flash Left, Spray Right.
    /// </summary>
    public void SelectFlashLeftSprayRight()
    {
        SetLayout(HandLayout.FlashLeftSprayRight);
    }

    /// <summary>
    /// Call this from a UI button to set layout option 2: Flash Right, Spray Left.
    /// </summary>
    public void SelectFlashRightSprayLeft()
    {
        SetLayout(HandLayout.FlashRightSprayLeft);
    }

    /// <summary>
    /// Programmatically set the layout.
    /// </summary>
    public void SetLayout(HandLayout layout)
    {
        currentLayout = layout;
        Debug.Log($"[HandAssignment] Layout set: {layout}");
        OnHandsAssigned?.Invoke(layout);
    }

    /// <summary>
    /// Returns the hand trigger axis for flashlight (index trigger).
    /// </summary>
    public float GetFlashlightTrigger()
    {
        return OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, FlashlightController);
    }

    /// <summary>
    /// Returns the hand trigger axis for spray (index trigger).
    /// </summary>
    public float GetSprayTrigger()
    {
        return OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, SprayController);
    }

    /// <summary>
    /// Returns the grip axis for flashlight hand.
    /// </summary>
    public float GetFlashlightGrip()
    {
        return OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, FlashlightController);
    }

    /// <summary>
    /// Returns the grip axis for spray hand.
    /// </summary>
    public float GetSprayGrip()
    {
        return OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, SprayController);
    }
}
