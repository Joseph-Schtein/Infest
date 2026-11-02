using UnityEngine;
using UnityEngine.Events;
using Meta.XR.MRUtilityKit;
using TMPro;

/// <summary>
/// Shows a prompt at game start asking the player whether to scan a new room
/// or reuse the existing scene data (for replaying in the same room).
/// After the room is ready, fires OnRoomReady for downstream systems.
/// </summary>
public class RoomScanPrompt : MonoBehaviour
{
    [Header("Events")]
    [Tooltip("Fired when the room data is ready (after scan or reuse)")]
    public UnityEvent OnRoomReady;

    [Header("Settings")]
    [Tooltip("If true, automatically reuse existing scan (skip the prompt)")]
    public bool autoReuse = false;

    [Tooltip("If true, automatically scan new room (skip the prompt)")]
    public bool autoScan = false;

    [Header("Darkness Slider")]
    [Tooltip("Darkest end of the slider (darkness shader brightness value)")]
    public float darknessMin = 0f;

    [Tooltip("Brightest end of the slider (darkness shader brightness value)")]
    public float darknessMax = 0.25f;

    [Tooltip("How fast the thumbstick moves the slider (fraction per second)")]
    public float sliderSpeed = 0.8f;

    private bool roomReady = false;
    private bool waitingForInput = false;
    private GameObject promptUI;
    private Camera promptCam;

    // ─── UI placement ───────────────────────────────────────────────────────────
    // All setup screens hang head-locked at this distance, scaled up so the
    // text is comfortably readable against passthrough.
    private const float UIDistance = 1.2f;
    private const float UIScale = 1.4f;

    // ─── Darkness slider state ──────────────────────────────────────────────────
    private const float TrackWidth = 0.5f;
    private bool sliderActive = false;
    private float sliderT = 0.5f;           // 0 = brightest, 1 = darkest
    private GameObject sliderUI;
    private TextMeshPro sliderValueText;
    private Transform sliderFill;
    private Renderer sliderFillRenderer;
    private DarknessActivator darknessActivator;
    private FlashlightController flashlightController;

    // Flashlight battery checkbox (shown on the darkness slider screen)
    private bool flashlightTimerEnabled = false; // default OFF (Yossi 2026-07-20); B on the darkness screen turns it on
    private TextMeshPro batteryToggleText;

    // ─── Game length slider state ───────────────────────────────────────────────
    private const int MinGameMinutes = 3;
    private const int MaxGameMinutes = 5;
    private bool durationActive = false;
    private int gameMinutes = 5;
    private bool stickReleased = true;      // edge detection for integer steps

    void Start()
    {
        if (autoReuse)
        {
            ReuseExistingScan();
            return;
        }

        if (autoScan)
        {
            ScanNewRoom();
            return;
        }

        // Default: wait for player choice via UI buttons calling
        // ScanNewRoom() or ReuseExistingScan()
        Debug.Log("[RoomScan] Waiting for player choice: Scan or Reuse");
        StartCoroutine(ShowPromptWhenTracking());
    }

    /// <summary>
    /// Waits until the HMD camera is actually tracking (position moves away
    /// from the origin) before spawning the prompt. Spawning directly in
    /// Start() places it at floor level at the world origin, out of view.
    /// </summary>
    private System.Collections.IEnumerator ShowPromptWhenTracking()
    {
        float timeout = Time.time + 3f;
        while (Time.time < timeout)
        {
            promptCam = FindHmdCamera();
            if (promptCam != null && promptCam.transform.position.sqrMagnitude > 0.01f)
                break;
            yield return null;
        }

        ShowPromptUI();
        waitingForInput = true;
    }

    private Camera FindHmdCamera()
    {
        Camera cam = Camera.main;
        if (cam != null) return cam;

        var centerEye = GameObject.Find("CenterEyeAnchor");
        if (centerEye != null) return centerEye.GetComponent<Camera>();
        return null;
    }

    /// <summary>
    /// Head-locked placement shared by all setup screens: parent to the
    /// camera so the UI is immune to tracking-space shifts (MRUK world lock
    /// moves the rig every frame, which makes any world-space follow logic
    /// oscillate). Sits at UIDistance and scaled by UIScale for readability.
    /// </summary>
    private void PlaceHeadLocked(GameObject ui)
    {
        if (promptCam == null) promptCam = FindHmdCamera();
        if (promptCam != null)
        {
            ui.transform.SetParent(promptCam.transform, false);
            ui.transform.localPosition = new Vector3(0f, 0f, UIDistance);
            ui.transform.localRotation = Quaternion.identity;
        }
        else
        {
            ui.transform.position = new Vector3(0f, 1.5f, UIDistance);
        }
        ui.transform.localScale = Vector3.one * UIScale;
    }

    private void ShowPromptUI()
    {
        promptUI = new GameObject("RoomScanPromptUI");
        PlaceHeadLocked(promptUI);

        // Build the text object
        var text = promptUI.AddComponent<TextMeshPro>();
        text.text = "ROOM SCAN\n\nPress  A  \u2192 Use last scan\nPress  B  \u2192 Scan new room";
        text.fontSize = 0.3f;
        text.alignment = TextAlignmentOptions.Center;
        text.color = Color.white;
        // CRITICAL: give the RectTransform a real size or TMP clips everything
        text.rectTransform.sizeDelta = new Vector2(1.8f, 1.2f);
        text.rectTransform.pivot = new Vector2(0.5f, 0.5f);

        // Dark panel behind the text for readability against passthrough
        CreateBackdrop(promptUI.transform, 0.7f, 0.42f);

        // Render on top of the darkness overlay so the prompt stays readable
        var renderer = promptUI.GetComponent<MeshRenderer>();
        if (renderer != null) renderer.sortingOrder = 1000;
    }

    void Update()
    {
        if (sliderActive)
        {
            HandleSliderInput();
            return;
        }

        if (durationActive)
        {
            HandleDurationInput();
            return;
        }

        if (!waitingForInput) return;

        // Button.One is typically the A button on right controller or X on left
        // Button.Two is typically the B button on right controller or Y on left
        if (OVRInput.GetDown(OVRInput.Button.One))
        {
            waitingForInput = false;
            if (promptUI != null) Destroy(promptUI);
            ReuseExistingScan();
        }
        else if (OVRInput.GetDown(OVRInput.Button.Two))
        {
            waitingForInput = false;
            if (promptUI != null) Destroy(promptUI);
            ScanNewRoom();
        }
    }

    /// <summary>
    /// Called by UI: Player wants to scan a new room.
    /// This triggers MRUK to request a fresh room capture.
    /// </summary>
    public async void ScanNewRoom()
    {
        Debug.Log("[RoomScan] Requesting new room scan...");

        if (MRUK.Instance != null)
        {
            // NO RegisterSceneLoadedCallback here: MRUK fires a registered
            // callback IMMEDIATELY when a scene is already loaded — and MRUK
            // can auto-load room data at startup, which made "scan new room"
            // jump straight to the setup sliders before Space Setup ever
            // opened. OnSceneLoaded is called explicitly after the awaited
            // load below instead.
            Debug.Log($"[RoomScan] Before scan: MRUK initialized={MRUK.Instance.IsInitialized}");

            // Clear existing room data (also resets IsInitialized)
            MRUK.Instance.ClearScene();

            // Explicitly launch Meta's Space Setup. LoadSceneFromDevice alone
            // only opens it when the device has NO room data — and the Quest
            // almost always has data from its initial setup, which would make
            // "scan new room" silently reuse the old scan.
            bool setupDone = await OVRScene.RequestSpaceSetup();
            Debug.Log($"[RoomScan] Space Setup completed: {setupDone}");

            var result = await MRUK.Instance.LoadSceneFromDevice(requestSceneCaptureIfNoDataFound: true);
            Debug.Log($"[RoomScan] LoadSceneFromDevice result: {result}");
        }
        else
        {
            Debug.LogWarning("[RoomScan] MRUK.Instance is null — cannot scan");
        }

        // Fire ready explicitly (even on failure, so the game doesn't hang)
        OnSceneLoaded();
    }

    /// <summary>
    /// Called by UI: Player wants to reuse the current room scan.
    /// </summary>
    public async void ReuseExistingScan()
    {
        Debug.Log("[RoomScan] Reusing existing room data...");

        if (MRUK.Instance != null)
        {
            // Check if scene data already exists
            if (MRUK.Instance.IsInitialized)
            {
                var room = MRUK.Instance.GetCurrentRoom();
                if (room != null)
                {
                    Debug.Log("[RoomScan] Existing room data found, using it");
                    OnSceneLoaded();
                    return;
                }
            }

            Debug.Log("[RoomScan] No existing data, falling back to device load");
            var result = await MRUK.Instance.LoadSceneFromDevice(requestSceneCaptureIfNoDataFound: true);
            Debug.Log($"[RoomScan] LoadSceneFromDevice result: {result}");
        }
        else
        {
            Debug.LogWarning("[RoomScan] MRUK.Instance is null");
        }

        OnSceneLoaded();
    }

    private void OnSceneLoaded()
    {
        if (roomReady) return; // Prevent double-fire
        roomReady = true;

        // Game length FIRST, darkness LAST: the darkness slider previews the
        // dimming live, so any screen shown after it would be unreadable at
        // high darkness settings
        Debug.Log("[RoomScan] Room is ready — showing game length slider");
        ShowDurationSlider();
    }

    // ─── Darkness Slider ────────────────────────────────────────────────────────

    /// <summary>
    /// LAST setup step (its live dimming preview would make any later screen
    /// unreadable): a head-locked slider controlling the darkness shader
    /// brightness between darknessMin (darkest) and darknessMax (brightest).
    /// Thumbstick adjusts, A confirms and starts the game flow.
    /// </summary>
    private void ShowDarknessSlider()
    {
        // Include inactive objects — the flashlight may be disabled until the
        // round starts, and its brightness field must still receive the choice
        // (otherwise it would overwrite the shader with its default on enable)
        darknessActivator = FindAnyObjectByType<DarknessActivator>(FindObjectsInactive.Include);
        flashlightController = FindAnyObjectByType<FlashlightController>(FindObjectsInactive.Include);

        // Start from the current brightness setting, mapped into slider space
        float current = darknessActivator != null ? darknessActivator.startingBrightness : darknessMax;
        current = Mathf.Clamp(current, darknessMin, darknessMax);
        sliderT = Mathf.InverseLerp(darknessMax, darknessMin, current);

        sliderUI = new GameObject("DarknessSliderUI");
        PlaceHeadLocked(sliderUI);

        // Dark panel behind the whole slider group for readability
        // (taller than the duration slider's — it also holds the battery checkbox)
        CreateBackdrop(sliderUI.transform, 0.72f, 0.7f, -0.1f);

        // Title
        var titleObj = new GameObject("Title");
        titleObj.transform.SetParent(sliderUI.transform, false);
        titleObj.transform.localPosition = new Vector3(0f, 0.16f, 0f);
        var title = titleObj.AddComponent<TextMeshPro>();
        title.text = "HOW DARK SHOULD IT GET?";
        title.fontSize = 0.28f;
        title.alignment = TextAlignmentOptions.Center;
        title.color = Color.white;
        title.rectTransform.sizeDelta = new Vector2(1.4f, 0.2f);
        titleObj.GetComponent<MeshRenderer>().sortingOrder = 1000;

        // Track (background bar)
        var track = CreateBarQuad("Track", sliderUI.transform,
            new Vector3(0f, 0f, 0.001f), new Vector3(TrackWidth, 0.045f, 1f),
            new Color(0.18f, 0.18f, 0.24f));

        // Fill (scales with the slider value)
        var fill = CreateBarQuad("Fill", sliderUI.transform,
            Vector3.zero, new Vector3(TrackWidth * 0.5f, 0.035f, 1f),
            new Color(0.6f, 0.4f, 0.9f));
        sliderFill = fill.transform;
        sliderFillRenderer = fill.GetComponent<Renderer>();

        // Value readout
        var valueObj = new GameObject("Value");
        valueObj.transform.SetParent(sliderUI.transform, false);
        valueObj.transform.localPosition = new Vector3(0f, -0.09f, 0f);
        sliderValueText = valueObj.AddComponent<TextMeshPro>();
        sliderValueText.fontSize = 0.24f;
        sliderValueText.alignment = TextAlignmentOptions.Center;
        sliderValueText.color = Color.white;
        sliderValueText.rectTransform.sizeDelta = new Vector2(1.4f, 0.15f);
        valueObj.GetComponent<MeshRenderer>().sortingOrder = 1000;

        // Hint
        var hintObj = new GameObject("Hint");
        hintObj.transform.SetParent(sliderUI.transform, false);
        hintObj.transform.localPosition = new Vector3(0f, -0.2f, 0f);
        var hint = hintObj.AddComponent<TextMeshPro>();
        hint.text = "Thumbstick ← brighter | darker →\nPress  A  to start";
        hint.fontSize = 0.18f;
        hint.alignment = TextAlignmentOptions.Center;
        hint.color = new Color(0.8f, 0.8f, 0.8f);
        hint.rectTransform.sizeDelta = new Vector2(1.4f, 0.25f);
        hintObj.GetComponent<MeshRenderer>().sortingOrder = 1000;

        // Flashlight battery checkbox — B toggles it
        var toggleObj = new GameObject("BatteryToggle");
        toggleObj.transform.SetParent(sliderUI.transform, false);
        toggleObj.transform.localPosition = new Vector3(0f, -0.34f, 0f);
        batteryToggleText = toggleObj.AddComponent<TextMeshPro>();
        batteryToggleText.fontSize = 0.16f;
        batteryToggleText.alignment = TextAlignmentOptions.Center;
        batteryToggleText.rectTransform.sizeDelta = new Vector2(1.4f, 0.15f);
        toggleObj.GetComponent<MeshRenderer>().sortingOrder = 1000;
        UpdateBatteryToggleVisual();

        UpdateSliderVisual();
        ApplyDarknessPreview();
        sliderActive = true;
    }

    private void UpdateBatteryToggleVisual()
    {
        if (batteryToggleText == null) return;
        batteryToggleText.text = flashlightTimerEnabled
            ? "[X] Flashlight battery (hold trigger to shine)  —  press B to toggle"
            : "[  ] Flashlight battery  —  press B to toggle";
        batteryToggleText.color = flashlightTimerEnabled
            ? new Color(1f, 0.8f, 0.3f)
            : new Color(0.6f, 0.6f, 0.6f);
    }

    private GameObject CreateBarQuad(string name, Transform parent, Vector3 localPos, Vector3 localScale, Color color)
    {
        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = name;
        Destroy(quad.GetComponent<Collider>());
        quad.transform.SetParent(parent, false);
        quad.transform.localPosition = localPos;
        quad.transform.localScale = localScale;

        var sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Unlit/Color");
        var mat = new Material(sh);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
        var rend = quad.GetComponent<MeshRenderer>();
        rend.sharedMaterial = mat;
        rend.sortingOrder = 999;
        return quad;
    }

    /// <summary>
    /// Dark panel behind a UI group so white text stays readable against
    /// bright passthrough. Rendered behind the tracks (999) and text (1000).
    /// </summary>
    private GameObject CreateBackdrop(Transform parent, float width, float height, float centerY = 0f)
    {
        var quad = CreateBarQuad("Backdrop", parent,
            new Vector3(0f, centerY, 0.01f), new Vector3(width, height, 1f),
            new Color(0.02f, 0.02f, 0.05f));
        quad.GetComponent<MeshRenderer>().sortingOrder = 990;
        return quad;
    }

    private void HandleSliderInput()
    {
        // Read whichever thumbstick is being pushed harder
        float x = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch).x;
        float rx = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch).x;
        if (Mathf.Abs(rx) > Mathf.Abs(x)) x = rx;

        if (Mathf.Abs(x) > 0.15f)
        {
            sliderT = Mathf.Clamp01(sliderT + x * sliderSpeed * Time.deltaTime);
            UpdateSliderVisual();
            ApplyDarknessPreview();
        }

        // B / Y toggles the flashlight battery checkbox
        if (OVRInput.GetDown(OVRInput.Button.Two) || OVRInput.GetDown(OVRInput.Button.Four))
        {
            flashlightTimerEnabled = !flashlightTimerEnabled;
            UpdateBatteryToggleVisual();
        }

        if (OVRInput.GetDown(OVRInput.Button.One))
        {
            ConfirmDarkness();
        }
    }

    private float CurrentBrightness()
    {
        // sliderT: 0 = brightest (darknessMax), 1 = darkest (darknessMin)
        return Mathf.Lerp(darknessMax, darknessMin, sliderT);
    }

    private void UpdateSliderVisual()
    {
        if (sliderFill != null)
        {
            float w = Mathf.Max(0.001f, TrackWidth * sliderT);
            sliderFill.localScale = new Vector3(w, 0.035f, 1f);
            sliderFill.localPosition = new Vector3(-TrackWidth / 2f + w / 2f, 0f, 0f);
        }

        if (sliderFillRenderer != null)
        {
            // Warm yellow when bright, deep violet when dark
            Color c = Color.Lerp(new Color(1f, 0.85f, 0.35f), new Color(0.45f, 0.2f, 0.85f), sliderT);
            var mat = sliderFillRenderer.sharedMaterial;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);
        }

        if (sliderValueText != null)
        {
            sliderValueText.text = $"Darkness: {Mathf.RoundToInt(sliderT * 100f)}%";
        }
    }

    private void ApplyDarknessPreview()
    {
        // Live preview so the player sees the room dim while sliding.
        // Three targets must stay in sync:
        //  - the overlay material: its _Brightness property is what the
        //    shader actually reads (a material property overrides the global)
        //  - the FlashlightController field: it re-pushes brightness every
        //    LateUpdate and would otherwise overwrite the choice
        //  - the global float: kept for any other listener
        float b = CurrentBrightness();

        var overlayMat = FlashlightController.FindDarknessOverlayMaterial();
        if (overlayMat != null)
            overlayMat.SetFloat("_Brightness", b);

        if (flashlightController != null)
            flashlightController.brightness = b;
        Shader.SetGlobalFloat("_Brightness", b);
    }

    private void ConfirmDarkness()
    {
        sliderActive = false;
        float brightness = CurrentBrightness();

        if (darknessActivator != null)
        {
            darknessActivator.startingBrightness = brightness;
            darknessActivator.BrightnessLevel = brightness;
        }

        // Write through the flashlight controller too — it owns the shader
        // global and overwrites it every LateUpdate
        if (flashlightController != null)
        {
            flashlightController.brightness = brightness;
            flashlightController.SetBatteryEnabled(flashlightTimerEnabled);
        }
        Shader.SetGlobalFloat("_Brightness", brightness);

        Debug.Log($"[RoomScan] Flashlight battery: {(flashlightTimerEnabled ? "ON" : "OFF")}");

        if (sliderUI != null) Destroy(sliderUI);

        Debug.Log($"[RoomScan] Darkness confirmed: brightness={brightness:F3}");

        // Darkness is the last setup screen — the game starts now. (No
        // battery-duration screen: the flashlight keeps its configured
        // batterySeconds, and the in-game gauge above the tool shows what's
        // left — same as the spray can's ammo gauge.)
        OnRoomReady?.Invoke();
    }

    // ─── Game Length Slider ─────────────────────────────────────────────────────

    /// <summary>
    /// First setup step after the scan: choose how long the round lasts
    /// (whole minutes only). Thumbstick steps the value, A confirms and
    /// moves on to the darkness slider.
    /// </summary>
    private void ShowDurationSlider()
    {
        // Start from the manager's current setting
        if (InfestGameManager.Instance != null)
        {
            gameMinutes = Mathf.Clamp(
                Mathf.RoundToInt(InfestGameManager.Instance.gameDurationSeconds / 60f),
                MinGameMinutes, MaxGameMinutes);
        }

        sliderUI = new GameObject("GameLengthSliderUI");
        PlaceHeadLocked(sliderUI);

        // Dark panel behind the whole slider group for readability
        CreateBackdrop(sliderUI.transform, 0.72f, 0.55f, -0.04f);

        // Title
        var titleObj = new GameObject("Title");
        titleObj.transform.SetParent(sliderUI.transform, false);
        titleObj.transform.localPosition = new Vector3(0f, 0.16f, 0f);
        var title = titleObj.AddComponent<TextMeshPro>();
        title.text = "GAME LENGTH";
        title.fontSize = 0.28f;
        title.alignment = TextAlignmentOptions.Center;
        title.color = Color.white;
        title.rectTransform.sizeDelta = new Vector2(1.4f, 0.2f);
        titleObj.GetComponent<MeshRenderer>().sortingOrder = 1000;

        // Track + fill
        CreateBarQuad("Track", sliderUI.transform,
            new Vector3(0f, 0f, 0.001f), new Vector3(TrackWidth, 0.045f, 1f),
            new Color(0.18f, 0.18f, 0.24f));
        var fill = CreateBarQuad("Fill", sliderUI.transform,
            Vector3.zero, new Vector3(TrackWidth * 0.5f, 0.035f, 1f),
            new Color(0.7f, 0.15f, 0.15f));
        sliderFill = fill.transform;
        sliderFillRenderer = fill.GetComponent<Renderer>();

        // Value readout
        var valueObj = new GameObject("Value");
        valueObj.transform.SetParent(sliderUI.transform, false);
        valueObj.transform.localPosition = new Vector3(0f, -0.09f, 0f);
        sliderValueText = valueObj.AddComponent<TextMeshPro>();
        sliderValueText.fontSize = 0.24f;
        sliderValueText.alignment = TextAlignmentOptions.Center;
        sliderValueText.color = Color.white;
        sliderValueText.rectTransform.sizeDelta = new Vector2(1.4f, 0.15f);
        valueObj.GetComponent<MeshRenderer>().sortingOrder = 1000;

        // Hint
        var hintObj = new GameObject("Hint");
        hintObj.transform.SetParent(sliderUI.transform, false);
        hintObj.transform.localPosition = new Vector3(0f, -0.2f, 0f);
        var hint = hintObj.AddComponent<TextMeshPro>();
        hint.text = "Thumbstick ← shorter | longer →\nPress  A  to confirm";
        hint.fontSize = 0.18f;
        hint.alignment = TextAlignmentOptions.Center;
        hint.color = new Color(0.8f, 0.8f, 0.8f);
        hint.rectTransform.sizeDelta = new Vector2(1.4f, 0.25f);
        hintObj.GetComponent<MeshRenderer>().sortingOrder = 1000;

        UpdateDurationVisual();
        stickReleased = true;
        durationActive = true;
    }

    private void HandleDurationInput()
    {
        float x = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch).x;
        float rx = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch).x;
        if (Mathf.Abs(rx) > Mathf.Abs(x)) x = rx;

        // Integer stepping with edge detection: one push = one step
        if (stickReleased && Mathf.Abs(x) > 0.6f)
        {
            gameMinutes = Mathf.Clamp(gameMinutes + (x > 0f ? 1 : -1), MinGameMinutes, MaxGameMinutes);
            stickReleased = false;
            UpdateDurationVisual();
        }
        else if (Mathf.Abs(x) < 0.3f)
        {
            stickReleased = true;
        }

        if (OVRInput.GetDown(OVRInput.Button.One))
        {
            ConfirmDuration();
        }
    }

    private void UpdateDurationVisual()
    {
        float t = (gameMinutes - MinGameMinutes) / (float)(MaxGameMinutes - MinGameMinutes);

        if (sliderFill != null)
        {
            float w = Mathf.Max(0.001f, TrackWidth * Mathf.Max(0.05f, t));
            sliderFill.localScale = new Vector3(w, 0.035f, 1f);
            sliderFill.localPosition = new Vector3(-TrackWidth / 2f + w / 2f, 0f, 0f);
        }

        if (sliderValueText != null)
        {
            sliderValueText.text = gameMinutes == 1 ? "1 minute" : $"{gameMinutes} minutes";
        }
    }

    private void ConfirmDuration()
    {
        durationActive = false;

        if (InfestGameManager.Instance != null)
        {
            InfestGameManager.Instance.gameDurationSeconds = gameMinutes * 60f;
        }

        if (sliderUI != null) Destroy(sliderUI);

        Debug.Log($"[RoomScan] Game length confirmed: {gameMinutes} minutes");
        ShowDarknessSlider();
    }
}
