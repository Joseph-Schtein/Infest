using UnityEngine;

/// <summary>
/// Controls the darkness overlay level and ties it to the flashlight.
///
/// HOW IT WORKS:
///   - Set BrightnessLevel (0–1) to control how bright the room is.
///   - 0 = complete darkness  (beam stands out dramatically)
///   - 1 = fully bright room  (no shader effect, beam barely visible)
///   - The darkness quad renderer is enabled automatically when brightness < 1.
///
/// SETUP:
///   1. Attach this script to any persistent GameObject (e.g. GameManager).
///   2. Assign the FlashlightController reference.
///   3. Assign the DarknessQuad MeshRenderer (the fullscreen darkness overlay) — 
///      or leave null if using URP FullScreenPassRendererFeature (recommended).
///   4. Set StartingBrightness to your desired starting level.
/// </summary>
public class DarknessActivator : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The FlashlightController that drives the darkness shader.")]
    public FlashlightController flashlightController;

    [Tooltip("The MeshRenderer on the DarknessQuad GameObject. " +
             "Leave null if using URP FullScreenPassRendererFeature.")]
    public MeshRenderer darknessQuadRenderer;

    [Header("Settings")]
    [Range(0f, 1f)]
    [Tooltip("0 = complete darkness. 1 = fully bright (no shader). " +
             "Default 0.3 gives a dim room where the flashlight beam is dramatic.")]
    public float startingBrightness = 0.3f;

    // ── Runtime API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets the global brightness level (0–1).
    /// 0 = complete darkness, 1 = fully bright (no shader effect).
    /// Setting this also updates the shader immediately.
    /// </summary>
    public float BrightnessLevel
    {
        get => flashlightController != null ? flashlightController.brightness : 1f;
        set => SetBrightness(value);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    void Start()
    {
        // Auto-find FlashlightController in scene if not assigned
        if (flashlightController == null)
            flashlightController = FindFirstObjectByType<FlashlightController>();

        if (flashlightController == null)
        {
            Debug.LogWarning("[DarknessActivator] No FlashlightController found in scene!");
            return;
        }

        // Auto-find DarknessQuad renderer if not assigned
        if (darknessQuadRenderer == null)
        {
            var go = GameObject.Find("DarknessQuad");
            if (go != null)
                darknessQuadRenderer = go.GetComponent<MeshRenderer>();
        }

        SetBrightness(startingBrightness);
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private void SetBrightness(float level)
    {
        level = Mathf.Clamp01(level);

        if (flashlightController != null)
            flashlightController.brightness = level;

        // Enable the overlay only when there is actual darkness to show
        // (brightness < ~1 means some darkening is applied)
        if (darknessQuadRenderer != null)
            darknessQuadRenderer.enabled = level < 0.999f;

        // Also push to shader immediately (don't wait for FlashlightController's timer)
        Shader.SetGlobalFloat("_Brightness", level);

        // The overlay shader declares _Brightness as a material Property, so the
        // material-level value overrides the global — must write to the material too.
        var overlayMat = FlashlightController.FindDarknessOverlayMaterial();
        if (overlayMat != null)
            overlayMat.SetFloat("_Brightness", level);
    }
}
