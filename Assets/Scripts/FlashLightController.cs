using UnityEngine;

/// <summary>
/// Bridges your 3D Spotlight into the fullscreen darkness shader,
/// and detects cockroaches within the beam cone using Physics raycasting.
///
/// HOW IT WORKS:
///   - The Unity Spotlight illuminates 3D geometry (normal Unity lighting).
///   - The darkness shader is a 2D screen-space overlay that knows nothing
///     about Unity lights. This script reads the Spotlight's position, angle,
///     and range, then feeds that data to the shader each frame.
///   - It also runs a cone-shaped Physics query to find CockroachBehavior
///     components inside the beam and calls OnLightHit() on them.
///
/// SETUP:
///   1. Attach this script to your flashlight GameObject (or its parent).
///   2. Assign your Spotlight component — the script will auto-read
///      SpotAngle and Range from it.
///   3. Make sure cockroach GameObjects have the tag "Cockroach".
///
/// On Start, if a Grabbable / Rigidbody / Collider is found on this
/// object, those components are stripped so the flashlight follows the
/// hand anchor permanently via the transform hierarchy.
/// </summary>
public class FlashlightController : MonoBehaviour
{
    // ── Flashlight Source ────────────────────────────────────────────────────

    [Header("Flashlight Source")]
    [Tooltip("The tip of your flashlight / controller. Defaults to this Transform if left empty.")]
    public Transform flashlightTransform;

    [Tooltip("Your Spotlight component. Script will auto-read SpotAngle + Range from it. " +
             "If left empty, uses the manual Cone Settings below instead.")]
    public Light spotLight;

    // ── Shader Properties ────────────────────────────────────────────────────

    [Header("Shader Properties")]
    [Tooltip("Global shader property for the light's screen-space position (Vector4).")]
    public string shaderLightPosName     = "_LightPos";

    [Tooltip("Global shader property for room brightness (Float 0-1). 0 = complete darkness, 1 = fully bright.")]
    public string shaderBrightnessName   = "_Brightness";

    [Tooltip("Global shader property hint for the hole radius in the darkness shader (Float). " +
             "Expose '_LightRadius' as a global property in your Shader Graph to use this.")]
    public string shaderLightRadiusName  = "_LightRadius";

    [Tooltip("Global shader property for the flashlight forward direction in world space (Vector4). " +
             "Used by DarknessOverlay.shader for the 3D cone reveal.")]
    public string shaderLightDirName     = "_LightDir";

    [Tooltip("Global shader property for cos(halfAngle) — the cone threshold used by DarknessOverlay.shader.")]
    public string shaderLightCosAngleName = "_LightCosAngle";

    [Tooltip("Global shader property for the flashlight world-space position (Vector4).")]
    public string shaderLightWorldPosName = "_LightWorldPos";

    [Tooltip("Global shader property for the light offset from camera (Vector4). " +
             "Used to reconstruct light position in sync with XR ATW-reprojected camera.")]
    public string shaderLightOffsetFromCamName = "_LightOffsetFromCam";

    [Tooltip("Global shader property for beam range in world units (Float).")]
    public string shaderBeamRangeName    = "_BeamRange";

    // ── Brightness ───────────────────────────────────────────────────────────

    [Header("Brightness")]
    [Range(0f, 1f)]
    [Tooltip("0 = complete darkness (pitch black). 1 = fully bright (no shader effect). " +
             "Adjust at runtime to change atmosphere.")]
    public float brightness = 0.3f;

    // ── Battery ──────────────────────────────────────────────────────────────

    [Header("Battery")]
    [Tooltip("Battery on/off — set by the checkbox on the darkness slider. " +
             "When off, the flashlight never runs out.")]
    public bool batteryEnabled = true;

    [Tooltip("Seconds of light per round — the beam dies when this runs out. " +
             "Refills when a new game starts.")]
    public float batterySeconds = 120f;

    [Tooltip("World-space offset of the battery gauge above the flashlight")]
    public Vector3 batteryGaugeOffset = new Vector3(0f, 0.1f, 0f);

    [Tooltip("Hold the flashlight hand's index trigger to shine — mirrors the " +
             "spray can, and the battery drains only while the light is on. " +
             "Only applies while the battery timer is enabled; without a " +
             "timer the light simply stays on.")]
    public bool triggerControlsLight = true;

    [Tooltip("How far the index trigger must be pressed to turn the light on (0-1)")]
    [Range(0f, 1f)]
    public float lightTriggerThreshold = 0.5f;

    private bool lightOn = true;
    private bool toolOccluded = false;
    private float occlusionTimer = 0f;
    private bool anchorCached = false;
    private Transform anchorParent;
    private Vector3 anchorLocalPos;
    private Quaternion anchorLocalRot;
    private bool detachedFromHand = false;
    private float batteryRemaining;
    private float effectiveBatterySeconds;  // base budget + game-length bonus
    private bool batteryReported = false;
    private SprayGauge batteryGauge;

    /// <summary>True when the round's light budget is spent (never true with the battery disabled).</summary>
    public bool BatteryEmpty => batteryEnabled && batteryRemaining <= 0f;

    /// <summary>True while the beam is actually shining (trigger held, battery not spent).</summary>
    public bool LightOn => lightOn;

    /// <summary>
    /// Turns the battery mechanic on/off — called by the setup checkbox.
    /// </summary>
    public void SetBatteryEnabled(bool enabled)
    {
        batteryEnabled = enabled;
        if (batteryGauge != null) batteryGauge.gameObject.SetActive(enabled);
        if (!enabled && spotLight != null) spotLight.enabled = true;
    }

    // ── Cone Settings (manual fallback if no Spotlight assigned) ─────────────

    [Header("Cone Settings — auto-read from Spotlight if assigned")]
    [Tooltip("Half-angle of the cone in degrees. Overridden by Spotlight.spotAngle / 2 if Spotlight is set.")]
    [Range(1f, 89f)]
    public float coneHalfAngle = 20f;

    [Tooltip("How far the beam reaches. Overridden by Spotlight.range if Spotlight is set.")]
    public float beamRange = 5f;

    [Tooltip("Multiplier on the cone half-angle used for SPOOKING roaches. " +
             "The darkness overlay reveals with a soft edge, so roaches sitting " +
             "in the visible penumbra were lit to the player but never counted " +
             "as hit — and just sat there. Anything the player can see in the " +
             "beam should react. 1 = exactly the light cone.")]
    [Range(1f, 1.5f)]
    public float spookAngleMargin = 1.15f;

    // ── Detection ────────────────────────────────────────────────────────────

    [Header("Beam Detection")]
    [Tooltip("Layer mask for cockroach GameObjects. Set to only the cockroach layer for best performance.")]
    public LayerMask cockroachLayerMask = 1 << 7; // Layer 7 = Cockroach

    [Tooltip("Enable line-of-sight check — walls will block the beam from reaching hidden cockroaches.")]
    public bool useLineOfSight = true;

    [Tooltip("Layer mask for obstacles (walls, furniture) that block the beam. Only used if Line Of Sight is enabled.")]
    public LayerMask obstacleLayerMask = ~0;

    [Tooltip("How often to run the detection (seconds). 0.05 = 20 checks/sec.")]
    [Range(0f, 0.2f)]
    public float detectionInterval = 0.05f;

    // ── Internals ────────────────────────────────────────────────────────────

    private Camera mainCamera;
    private float  detectionTimer;

    // The darkness overlay material declares _Brightness as a material
    // property, and a material's own value always overrides the shader
    // global — so brightness must be written onto the material itself.
    private static Material darknessOverlayMat;
    private static bool darknessMatWarned;

    /// <summary>
    /// Finds the DarknessOverlay material used by the URP fullscreen pass
    /// (searched once among loaded assets, cached afterwards).
    /// </summary>
    public static Material FindDarknessOverlayMaterial()
    {
        if (darknessOverlayMat != null) return darknessOverlayMat;
        // Don't cache failure — material may not be loaded yet on the first call

        foreach (var mat in Resources.FindObjectsOfTypeAll<Material>())
        {
            if (mat != null && mat.shader != null && mat.shader.name == "Custom/DarknessOverlay")
            {
                darknessOverlayMat = mat;
                break;
            }
        }

        if (darknessOverlayMat == null && !darknessMatWarned)
        {
            darknessMatWarned = true;
            Debug.LogWarning("[Flashlight] DarknessOverlay material not found yet — will retry");
        }
        return darknessOverlayMat;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    void Start()
    {
        // Try to find the main camera with fallbacks for Quest/XR
        mainCamera = Camera.main;

        // Fallback 1: OVR CenterEyeAnchor (Quest camera rig)
        if (mainCamera == null)
        {
            var centerEye = GameObject.Find("CenterEyeAnchor");
            if (centerEye != null)
                mainCamera = centerEye.GetComponent<Camera>();
        }

        // Fallback 2: any camera in the scene
        if (mainCamera == null)
            mainCamera = FindFirstObjectByType<Camera>();

        // Fallback: use this object's own transform if none assigned
        if (flashlightTransform == null)
            flashlightTransform = transform;

        // Try to find a Spotlight on this object or children automatically
        if (spotLight == null)
            spotLight = GetComponentInChildren<Light>();

        // The spotlight must not re-light the roaches — its warm light shifted
        // their colour whenever the beam touched them. Roaches are revealed by
        // the darkness-overlay cone instead, keeping their natural look;
        // detection/spooking is separate physics logic and is unaffected.
        if (spotLight != null)
        {
            int roachLayer = LayerMask.NameToLayer("Cockroach");
            if (roachLayer < 0) roachLayer = 7; // project default
            spotLight.cullingMask &= ~(1 << roachLayer);
        }

        // ── Strip grab components — flashlight is permanently attached to hand ──
        RemoveGrabComponents(gameObject);

        // ── Battery + gauge (mirrors the spray can's ammo gauge) ────────────
        effectiveBatterySeconds = batterySeconds;
        batteryRemaining = effectiveBatterySeconds;
        // Attach() compensates for the flashlight model's tiny scale
        // (~0.0002) — a raw child gauge was microns wide and invisible
        batteryGauge = SprayGauge.Attach(transform, batteryGaugeOffset);
        batteryGauge.gameObject.SetActive(batteryEnabled);

        // Refill whenever a new game starts (including restarts)
        if (InfestGameManager.Instance != null)
        {
            InfestGameManager.Instance.OnGameStarted.AddListener(ResetBattery);
        }

        // Initial sync so values are correct on frame 1
        SyncSpotlightProperties();
        UpdateShader(); // Push globals immediately — prevents grey flash before first timer tick
    }

    /// <summary>
    /// Refills the battery — called when a new game starts. Longer games
    /// grant a bigger budget (+30s per minute of game length above baseline).
    /// </summary>
    public void ResetBattery()
    {
        effectiveBatterySeconds = batterySeconds;
        if (InfestGameManager.Instance != null)
            effectiveBatterySeconds += InfestGameManager.Instance.ResourceBonusSeconds;

        batteryRemaining = effectiveBatterySeconds;
        batteryReported = false;
        if (spotLight != null) spotLight.enabled = true;
        if (batteryGauge != null) batteryGauge.SetFill(1f);

        Debug.Log($"[Flashlight] Battery for this round: {effectiveBatterySeconds}s");
    }

    void Update()
    {
        float dt = Time.deltaTime;
        detectionTimer += dt;

        // Retry finding camera if it wasn't available at Start
        if (mainCamera == null)
        {
            mainCamera = Camera.main;
            if (mainCamera == null)
            {
                var centerEye = GameObject.Find("CenterEyeAnchor");
                if (centerEye != null)
                    mainCamera = centerEye.GetComponent<Camera>();
            }
        }

        // ── Stay anchored to the CONTROLLER, not the hand ────────────────────
        // When both controllers are set down the headset switches to hand
        // tracking and the hand anchors follow the player's bare hands — the
        // flashlight would glue itself to their hand. Detach and freeze at
        // the controller's resting pose instead; re-attach on pickup.
        // (Cached on the first Update so FlashlightGrabController has already
        // applied its local offsets.)
        if (!anchorCached)
        {
            anchorCached = true;
            anchorParent = transform.parent;
            anchorLocalPos = transform.localPosition;
            anchorLocalRot = transform.localRotation;
        }
        bool resting = ControllerActivity.BothControllersResting;
        if (resting != detachedFromHand)
        {
            detachedFromHand = resting;
            if (resting)
            {
                transform.SetParent(null, true);   // freeze where the controller lies
            }
            else if (anchorParent != null)
            {
                transform.SetParent(anchorParent, false);
                transform.localPosition = anchorLocalPos;
                transform.localRotation = anchorLocalRot;
            }
        }

        // ── Fake occlusion by real furniture ─────────────────────────────────
        // Passthrough furniture can't hide virtual objects by itself. When a
        // furniture volume or wall blocks the player's view of the controller
        // (e.g. it was dropped behind the sofa), the whole flashlight
        // disappears and the light goes out — you can't see the real
        // controller, so you can't see the virtual tool either.
        occlusionTimer -= dt;
        if (occlusionTimer <= 0f)
        {
            occlusionTimer = 0.1f;
            bool blocked = mainCamera != null && flashlightTransform != null &&
                FurnitureCover.IsSegmentBlocked(mainCamera.transform.position, flashlightTransform.position);
            if (blocked != toolOccluded)
            {
                toolOccluded = blocked;
                // Fresh fetch on every toggle: the gauge builds its quads
                // lazily, so a list cached at Start could miss them
                foreach (var r in GetComponentsInChildren<Renderer>(true))
                    if (r != null) r.enabled = !blocked;
            }
        }

        // ── Light on/off — hold the trigger to shine, like the spray ─────────
        // Only when the battery timer is enabled; otherwise the light stays on.
        // An occluded flashlight is out of play — no beam, no spooking, no drain.
        bool wantOn = !toolOccluded;
        if (wantOn && batteryEnabled && triggerControlsLight)
            wantOn = ReadFlashlightTrigger() > lightTriggerThreshold;
        lightOn = wantOn && !BatteryEmpty;
        if (spotLight != null && spotLight.enabled != lightOn)
            spotLight.enabled = lightOn;

        // ── Battery drain (only while the light is ON during a round) ────────
        if (lightOn && batteryEnabled
            && InfestGameManager.Instance != null
            && InfestGameManager.Instance.IsGameActive)
        {
            batteryRemaining -= dt;
            if (batteryGauge != null)
                batteryGauge.SetFill(batteryRemaining / Mathf.Max(1f, effectiveBatterySeconds));

            if (batteryRemaining <= 0f && !batteryReported)
            {
                batteryReported = true;
                batteryRemaining = 0f;
                lightOn = false;
                if (spotLight != null) spotLight.enabled = false;
                Debug.Log("[Flashlight] Battery dead — the light is gone");
            }
        }

        // ── Throttled cone raycast to detect cockroaches ─────────────────────
        // A switched-off or dead flashlight can't spook anything
        if (lightOn && detectionTimer >= detectionInterval)
        {
            detectionTimer = 0f;
            DetectCockroachesInCone();
        }
    }

    /// <summary>
    /// Index trigger value of whichever hand holds the flashlight.
    /// Raw axis first for reliability, mapped input as fallback (same as the spray).
    /// </summary>
    private float ReadFlashlightTrigger()
    {
        OVRInput.Controller ctrl = HandAssignmentManager.Instance != null
            ? HandAssignmentManager.Instance.FlashlightController
            : OVRInput.Controller.LTouch;

        float v = ctrl == OVRInput.Controller.LTouch
            ? OVRInput.Get(OVRInput.RawAxis1D.LIndexTrigger)
            : OVRInput.Get(OVRInput.RawAxis1D.RIndexTrigger);
        if (v <= 0.01f)
            v = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, ctrl);
        return v;
    }

    /// <summary>
    /// Shader updates run in LateUpdate so we read the flashlight transform
    /// AFTER all Update() calls (including the XR tracking system and any
    /// remaining grab logic) have finished moving it. This eliminates the
    /// one-frame positional lag that caused the beam to stutter.
    /// </summary>
    void LateUpdate()
    {
        SyncSpotlightProperties();
        UpdateShader();
    }

    // ── Private Methods ───────────────────────────────────────────────────────

    /// <summary>
    /// Beam cone width multiplier (0.75 = 25% tighter → a more atmospheric,
    /// focused beam). Deliberately a const, NOT an inspector field: adding a
    /// serialized field mid-class shifts FlashlightController's serialization
    /// layout, which breaks incremental player builds ("script class layout is
    /// incompatible between the editor and the player"). A const changes no
    /// layout. Edit this number to widen (→1) or tighten (→0) the beam.
    /// </summary>
    private const float BeamAngleScale = 0.75f;

    /// <summary>
    /// Reads SpotAngle and Range from the assigned Spotlight so the cone
    /// detection always matches the visual light beam.
    /// </summary>
    private void SyncSpotlightProperties()
    {
        if (spotLight == null || spotLight.type != LightType.Spot) return;

        // Unity's spotAngle is the full angle; we want the half-angle for the cone check.
        // BeamAngleScale tightens the cone — both the visible beam AND the spook
        // detection, so they stay in agreement (a tighter beam that also spooks
        // a tighter area).
        coneHalfAngle = spotLight.spotAngle * 0.5f * BeamAngleScale;
        beamRange     = spotLight.range;
    }

    /// <summary>
    /// Pushes flashlight data to global shader properties each frame.
    /// World-space cone data is ALWAYS sent (no camera dependency).
    /// Screen-space data is only sent when a camera is available.
    /// </summary>
    private void UpdateShader()
    {
        // Always update brightness (can be tweaked at runtime)
        // Convention: 0 = complete darkness, 1 = fully bright (no shader effect)
        // The global is kept for any listener, but the overlay material's own
        // property is what the shader actually uses — write both.
        Shader.SetGlobalFloat(shaderBrightnessName, brightness);
        var overlayMat = FindDarknessOverlayMaterial();
        if (overlayMat != null)
            overlayMat.SetFloat(shaderBrightnessName, brightness);

        if (flashlightTransform == null) return;

        // Light off (trigger released or battery dead): collapse the shader's
        // cone reveal (cos threshold > 1 can never pass, range 0) so the
        // darkness swallows the beam. The room darkness itself stays.
        if (!lightOn)
        {
            Shader.SetGlobalFloat(shaderLightCosAngleName, 2f);
            Shader.SetGlobalFloat(shaderBeamRangeName, 0f);
            return;
        }

        // ── World-space cone data ─────────────────────────────────────────────

        // Flashlight world position
        Vector3 worldPos = flashlightTransform.position;
        Shader.SetGlobalVector(shaderLightWorldPosName,
            new Vector4(worldPos.x, worldPos.y, worldPos.z, 0f));

        // Flashlight forward direction — the SAME vector the detection cone and
        // the Unity Spotlight use, so the visible beam and the gameplay cone
        // always agree. (An earlier "invertVerticalAim" flip made the shader
        // beam point somewhere the detection cone did not.)
        Vector3 worldDir = flashlightTransform.forward;
        Shader.SetGlobalVector(shaderLightDirName,
            new Vector4(worldDir.x, worldDir.y, worldDir.z, 0f));

        // cos(halfAngle) — pre-computed so shader skips per-pixel trig
        float cosHalf = Mathf.Cos(coneHalfAngle * Mathf.Deg2Rad);
        Shader.SetGlobalFloat(shaderLightCosAngleName, cosHalf);

        // Beam range (how far the cone reveal extends)
        Shader.SetGlobalFloat(shaderBeamRangeName, beamRange);

        // ── Screen-space data (optional, only if camera is available) ─────────

        if (mainCamera != null)
        {
            Vector3 screenPos = mainCamera.WorldToViewportPoint(flashlightTransform.position);
            if (screenPos.z < 0)
                screenPos = new Vector3(-10f, -10f, 0f);
            Shader.SetGlobalVector(shaderLightPosName,
                new Vector4(screenPos.x, screenPos.y, 0f, 0f));

            float radiusHint = Mathf.Tan(coneHalfAngle * Mathf.Deg2Rad) * 0.35f;
            Shader.SetGlobalFloat(shaderLightRadiusName, radiusHint);
        }
    }

    /// <summary>
    /// Cone detection in two phases:
    ///   1. Broad phase  — OverlapSphere (cheap, finds candidates inside beamRange)
    ///   2. Narrow phase — Dot-product angle check (is it inside the cone angle?)
    ///   3. Optional     — Linecast for line-of-sight (walls block the beam)
    /// Calls OnLightHit() on every CockroachBehavior found inside the cone.
    /// </summary>
    private float _debugLogTimer = 0f;

    // ── On-Screen Debug HUD (Embedded) ────────────────────────────────────────
    public bool showDebugHUD = true;
    private int _hudCandidates = 0;
    private int _hudHits       = 0;
    private string _hudBlocked = "";
    private string _hudLastEvent = "No detection yet";
    private float  _hudLastEventTime = 0f;
    private GUIStyle _labelStyle;

    void OnGUI()
    {
        if (!showDebugHUD) return;

        if (_labelStyle == null)
        {
            _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 28, fontStyle = FontStyle.Bold };
            _labelStyle.normal.textColor = Color.white;
        }

        float boxW = 680f, boxH = 260f, pad = 20f;
        GUI.color = new Color(0f, 0f, 0f, 0.65f);
        GUI.DrawTexture(new Rect(pad, pad, boxW, boxH), Texture2D.whiteTexture);
        GUI.color = Color.white;

        float x = pad * 2f, y = pad * 2f, lineH = 52f;

        _labelStyle.normal.textColor = Color.cyan;
        GUI.Label(new Rect(x, y, boxW, lineH), "▶ Flashlight Debug HUD", _labelStyle);
        y += lineH;

        _labelStyle.normal.textColor = _hudCandidates > 0 ? Color.green : Color.red;
        GUI.Label(new Rect(x, y, boxW, lineH), $"Candidates: {_hudCandidates}   Hits: {_hudHits}", _labelStyle);
        y += lineH;

        _labelStyle.normal.textColor = string.IsNullOrEmpty(_hudBlocked) ? Color.grey : Color.yellow;
        GUI.Label(new Rect(x, y, boxW, lineH), string.IsNullOrEmpty(_hudBlocked) ? "LoS: clear/disabled" : $"LoS BLOCKED by: {_hudBlocked}", _labelStyle);
        y += lineH;

        _labelStyle.normal.textColor = (Time.time - _hudLastEventTime) < 1f ? Color.green : Color.white;
        GUI.Label(new Rect(x, y, boxW, lineH), $"Last: {_hudLastEvent}", _labelStyle);
    }

    private void DetectCockroachesInCone()
    {
        if (flashlightTransform == null) return;

        Vector3 origin  = flashlightTransform.position;
        Vector3 forward = flashlightTransform.forward;

        // Spook slightly wider than the hard cone — the shader's reveal has a
        // soft edge, so a roach in the penumbra looks lit and must react
        float spookHalfAngle = Mathf.Min(89f, coneHalfAngle * Mathf.Max(1f, spookAngleMargin));
        float cosAngle       = Mathf.Cos(spookHalfAngle * Mathf.Deg2Rad);

        // ── Broad phase ─────────────────────────────────────────────────────────
        Collider[] candidates = Physics.OverlapSphere(origin, beamRange, cockroachLayerMask, QueryTriggerInteraction.Collide);

        if (candidates.Length == 0)
        {
            var allRoaches = GameObject.FindGameObjectsWithTag("Cockroach");
            if (allRoaches.Length > 0)
            {
                var fallbackList = new System.Collections.Generic.List<Collider>();
                foreach (var go in allRoaches)
                {
                    if (Vector3.Distance(origin, go.transform.position) <= beamRange)
                    {
                        var col = go.GetComponent<Collider>();
                        if (col != null) fallbackList.Add(col);
                    }
                }
                candidates = fallbackList.ToArray();

                _debugLogTimer += detectionInterval;
                if (_debugLogTimer >= 2f)
                {
                    _debugLogTimer = 0f;
                    Debug.LogWarning($"[Flashlight] Layer mask missed, but tag found {candidates.Length} in range. Layer is {allRoaches[0].layer}");
                }
            }
        }

        // Periodic status log + HUD update
        _debugLogTimer += detectionInterval;
        if (_debugLogTimer >= 1f)
        {
            _debugLogTimer = 0f;
            _hudCandidates = candidates.Length;
            Debug.Log($"[Flashlight] Detection: origin={origin}, fwd={forward}, halfAngle={coneHalfAngle:F1}°, " +
                      $"range={beamRange:F1}, layerMask={cockroachLayerMask.value}, candidates={candidates.Length}");
        }

        foreach (Collider col in candidates)
        {
            Vector3 targetCenter = col.bounds.center;
            float raiseAmount = Mathf.Max(0.05f, col.bounds.extents.y);
            Vector3 liftedTarget = new Vector3(targetCenter.x, col.bounds.min.y + raiseAmount, targetCenter.z);

            Vector3 toTarget = liftedTarget - origin;
            float distance = toTarget.magnitude;

            if (distance > 0.5f)
            {
                if (Vector3.Dot(forward, toTarget.normalized) < cosAngle) continue;
            }

            // Occlusion uses the MRUK furniture-volume/wall math — the same
            // test the roach visibility system uses — NOT a physics linecast:
            // scan-mesh floor bumps made linecasts eat legitimate hits, which
            // is why the original line-of-sight gate was removed. If the
            // player can't see the roach, the beam doesn't spook it either.
            if (FurnitureCover.IsSegmentBlocked(origin, liftedTarget))
            {
                _hudBlocked = $"{col.name}: furniture/wall volume";
                continue;
            }

            CockroachBehavior cockroach = col.GetComponent<CockroachBehavior>();
            if (cockroach == null) cockroach = col.GetComponentInParent<CockroachBehavior>();
            if (cockroach == null) cockroach = col.GetComponentInChildren<CockroachBehavior>();

            if (cockroach != null)
            {
                _hudHits++;
                _hudLastEvent = $"HIT {col.name} dist={distance:F2}m";
                _hudLastEventTime = Time.time;
                cockroach.OnLightHit(origin);
            }
            else
            {
                _hudLastEvent = $"Collider {col.name} has no script!";
                _hudLastEventTime = Time.time;
            }
        }
    }

    // ── Grab Component Cleanup ────────────────────────────────────────────────

    /// <summary>
    /// Removes all Grabbable (Oculus Interaction SDK), Rigidbody, and Collider
    /// components from a GameObject so it follows the hand anchor permanently.
    /// </summary>
    private void RemoveGrabComponents(GameObject target)
    {
        // 1. Disable and destroy all Oculus Interaction SDK MonoBehaviours
        //    (Grabbable, PointableUnityEventWrapper, PhysicsGrabbable, etc.)
        foreach (var mb in target.GetComponents<MonoBehaviour>())
        {
            if (mb == null || mb == this) continue; // Don't destroy ourselves!
            string ns = mb.GetType().Namespace ?? "";
            if (ns.StartsWith("Oculus.Interaction") || ns.StartsWith("Meta.XR"))
            {
                Debug.Log($"[Flashlight] Removing {mb.GetType().Name} from {target.name}");
                mb.enabled = false;
                Destroy(mb);
            }
        }

        // 2. Remove Rigidbody
        var rb = target.GetComponent<Rigidbody>();
        if (rb != null)
        {
            Debug.Log($"[Flashlight] Removing Rigidbody from {target.name}");
            Destroy(rb);
        }

        // 3. Remove grab-detection colliders
        foreach (var col in target.GetComponents<Collider>())
        {
            Debug.Log($"[Flashlight] Removing {col.GetType().Name} from {target.name}");
            Destroy(col);
        }
    }

    // ── Editor Visualization ──────────────────────────────────────────────────

    /// <summary>
    /// Draws a cone wireframe in the Scene view to visualize the detection area.
    /// Only visible when this GameObject is selected.
    /// </summary>
    void OnDrawGizmosSelected()
    {
        Transform t = flashlightTransform != null ? flashlightTransform : transform;

        Vector3 origin   = t.position;
        Vector3 fwd      = t.forward;
        Vector3 right    = t.right;
        Vector3 up       = t.up;
        float   halfRad  = coneHalfAngle * Mathf.Deg2Rad;
        float   coneRadius = Mathf.Tan(halfRad) * beamRange;
        Vector3 coneEnd  = origin + fwd * beamRange;

        // Cone outline (4 edge lines)
        Gizmos.color = new Color(1f, 0.9f, 0f, 0.8f);
        Gizmos.DrawLine(origin, coneEnd + right * coneRadius);
        Gizmos.DrawLine(origin, coneEnd - right * coneRadius);
        Gizmos.DrawLine(origin, coneEnd + up    * coneRadius);
        Gizmos.DrawLine(origin, coneEnd - up    * coneRadius);

        // Centre axis
        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(origin, coneEnd);

        // Circle at the cone tip (approximated with 24 segments)
        Gizmos.color = new Color(1f, 0.9f, 0f, 0.4f);
        int   segs   = 24;
        for (int i = 0; i < segs; i++)
        {
            float a0 = (i       / (float)segs) * Mathf.PI * 2f;
            float a1 = ((i + 1) / (float)segs) * Mathf.PI * 2f;
            Vector3 p0 = coneEnd + (right * Mathf.Cos(a0) + up * Mathf.Sin(a0)) * coneRadius;
            Vector3 p1 = coneEnd + (right * Mathf.Cos(a1) + up * Mathf.Sin(a1)) * coneRadius;
            Gizmos.DrawLine(p0, p1);
        }
    }
}
