using UnityEngine;

/// <summary>
/// Detects when the player has set BOTH controllers down (e.g. on a table).
/// Quest controllers stay tracked while resting, so "dropped" is a heuristic:
/// no capacitive touch anywhere, no button/trigger pressed, and the controller
/// physically still — held controllers always jitter with the hand. Both
/// controllers must stay idle for RestDelay seconds before this reports true,
/// and any touch/press/movement on either controller clears it instantly.
/// Shared by FlashlightController and SprayCanController to hide the
/// hand-anchored tools while nobody is holding the controllers.
/// </summary>
public static class ControllerActivity
{
    /// <summary>A controller moving slower than this counts as lying still (m/s).</summary>
    private const float RestVelocity = 0.05f;

    /// <summary>Both controllers must be idle this long before counting as dropped.</summary>
    private const float RestDelay = 1.5f;

    private static int lastFrame = -1;
    private static float restTimer;
    private static bool bothResting;

    /// <summary>True while both controllers have been set down.</summary>
    public static bool BothControllersResting
    {
        get { Refresh(); return bothResting; }
    }

    private static void Refresh()
    {
        // Evaluate once per frame no matter how many scripts ask
        if (Time.frameCount == lastFrame) return;
        lastFrame = Time.frameCount;

        if (IsHeld(OVRInput.Controller.LTouch) || IsHeld(OVRInput.Controller.RTouch))
        {
            restTimer = 0f;
            bothResting = false;
            return;
        }

        restTimer += Time.unscaledDeltaTime;
        if (restTimer >= RestDelay)
        {
            bothResting = true;
        }
    }

    private static bool IsHeld(OVRInput.Controller controller)
    {
        // Fingers on any capacitive sensor (thumbstick, buttons, trigger)
        if (OVRInput.Get(OVRInput.Touch.Any, controller)) return true;

        // Anything actually pressed
        if (OVRInput.Get(OVRInput.Button.Any, controller)) return true;

        // Physically moving — a controller on a table is dead still, a held
        // one never is
        if (OVRInput.GetLocalControllerVelocity(controller).magnitude > RestVelocity) return true;

        return false;
    }
}
