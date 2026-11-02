using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Spray can permanently attached to one controller hand via scene parenting.
/// The SprayLogic GameObject should be a CHILD of the hand anchor (e.g. RightHandAnchor).
/// This script only handles: spray trigger (index trigger) and cockroach kill detection.
/// Movement is automatic via Unity's transform hierarchy.
///
/// The particle system should be a CHILD of the spray can model in the scene.
/// Position and rotate it in the editor so it sits at the spray nozzle.
/// This script preserves that local offset automatically.
/// </summary>
public class SprayCanController : MonoBehaviour
{
    [Header("Visuals")]
    [Tooltip("Drag your BugSprayParticles here")]
    public ParticleSystem sprayParticles;



    [Header("Kill Settings")]
    [Tooltip("Damage dealt per spray particle hit")]
    public float damagePerHit = 1f;

    [Header("Spray Physics")]
    [Tooltip("How strongly gravity pulls the spray down (0 = flies straight, " +
             "1 = full gravity). Gives the jet a realistic downward arc.")]
    [Range(0f, 2f)]
    public float sprayGravity = 0.5f;

    [Header("Spray Trigger")]
    [Tooltip("How far the index trigger must be pressed to spray (0-1)")]
    [Range(0f, 1f)]
    public float sprayThreshold = 0.5f;

    [Header("Haptics")]
    [Tooltip("Vibration strength on the spray hand while spray is coming out " +
             "(0 = no vibration). Only the spray hand ever vibrates.")]
    [Range(0f, 1f)]
    public float sprayVibration = 0.35f;

    [Header("Spray Amount")]
    [Tooltip("Total seconds of spray for the whole round — once spent, the can is dry until the next game")]
    public float totalSpraySeconds = 90f;

    [Tooltip("World-space offset of the spray gauge above the can")]
    public Vector3 gaugeOffset = new Vector3(0f, 0.14f, 0f);

    // ─── Runtime state ───────────────────────────────────────────────────────────
    private OVRInput.Controller activeController = OVRInput.Controller.RTouch;
    private bool warmupDone = false;
    private float sprayAmount = 1f;       // 0..1 fraction of the budget remaining
    private float effectiveSpraySeconds;  // base budget + game-length bonus
    private SprayGauge gauge;
    private bool vibrating = false;
    private AudioSource sprayAudio;   // aerosol hiss while spraying (runtime — NOT serialized)
    private Camera occlusionCam;
    private bool toolOccluded = false;
    private float occlusionTimer = 0f;
    private bool anchorCached = false;
    private Transform anchorParent;
    private Vector3 anchorLocalPos;
    private Quaternion anchorLocalRot;
    private bool detachedFromHand = false;

    void Start()
    {
        // Base budget until a round starts and applies the game-length bonus
        effectiveSpraySeconds = totalSpraySeconds;

        // Determine which controller to read input from
        if (HandAssignmentManager.Instance != null)
        {
            activeController = HandAssignmentManager.Instance.SprayController;
        }

        if (sprayParticles != null)
        {
            // ── Reparent spray model if it's on a separate root object ────────
            // The particle system may live on a root-level grabbable (Spray_insect).
            // Move its entire root hierarchy under this hand-anchored transform
            // so particles always emit from the hand position.
            Transform sprayModelRoot = sprayParticles.transform.root;
            if (sprayModelRoot != transform.root)
            {
                Debug.Log($"[SprayCan] Reparenting spray model '{sprayModelRoot.name}' from root to {transform.name}");

                // Strip all grab-related components first (Grabbable, Rigidbody, Colliders)
                RemoveGrabComponents(sprayModelRoot.gameObject);

                // Reparent under this transform – worldPositionStays=true so
                // the visual mesh keeps its current local offset from the can.
                sprayModelRoot.SetParent(transform, true);
            }

            // The particle system is a child of the spray can model.
            // We do NOT override its local position/rotation here — use the
            // Unity Editor to place it at the spray nozzle opening.
            // It will automatically follow the controller via the hierarchy.

            // Kill any stale particles from editor / scene load.
            // playOnAwake may have started particles at the old world position.
            var main = sprayParticles.main;
            main.playOnAwake = false;

            // Gravity pulls the jet into a natural downward arc. Requires
            // world-space simulation — in local space the arc would bend
            // with the can instead of toward the floor.
            main.gravityModifier = sprayGravity;
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            sprayParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            sprayParticles.Clear(true);

            // ── Configure the Collision module so hits actually register ──────
            var collision = sprayParticles.collision;
            collision.enabled = true;
            collision.type = ParticleSystemCollisionType.World;
            collision.sendCollisionMessages = true;   // required for OnParticleCollision
            collision.enableDynamicColliders = true;  // roaches have (kinematic) rigidbodies
            collision.lifetimeLoss = 1f;              // particle dies on impact instead of bouncing
            collision.bounce = 0f;                    // no ricochet before it disappears

            // Make sure the Cockroach layer is in the collide mask
            int cockroachLayer = LayerMask.NameToLayer("Cockroach");
            if (cockroachLayer < 0) cockroachLayer = 7; // project default
            collision.collidesWith = collision.collidesWith | (1 << cockroachLayer);

            // The camera rig carries body colliders (including a head-to-floor
            // capsule). With lifetimeLoss = 1, aiming DOWN spawns particles
            // inside that capsule and they die at the nozzle — the gauge
            // drains but no spray is ever visible. Park every rig collider on
            // Ignore Raycast and keep that layer out of the collide mask.
            collision.collidesWith = collision.collidesWith & ~(1 << 2); // 2 = Ignore Raycast
            foreach (var rigCol in transform.root.GetComponentsInChildren<Collider>(true))
            {
                if (rigCol.gameObject.layer == cockroachLayer) continue;
                if (rigCol.gameObject.layer != 2)
                {
                    Debug.Log($"[SprayCan] Rig collider '{rigCol.name}' → Ignore Raycast so the spray passes through the player");
                    rigCol.gameObject.layer = 2;
                }
            }

            // OnParticleCollision is only delivered to the ParticleSystem's own
            // GameObject (a child of the can model), not to this controller —
            // attach the kill relay there.
            var relay = sprayParticles.GetComponent<SprayParticleKillRelay>();
            if (relay == null)
            {
                relay = sprayParticles.gameObject.AddComponent<SprayParticleKillRelay>();
            }
            relay.damagePerHit = damagePerHit;
        }

        // ── Spray-amount gauge floating above the can ───────────────────────
        gauge = SprayGauge.Attach(transform, gaugeOffset);

        // ── Spray hiss (aerosol sound played while spraying) ────────────────
        // Runtime AudioSource + private field, so NO serialized field is added
        // to this class — adding one shifts the serialization layout and breaks
        // incremental player builds ("script class layout is incompatible").
        // Prefers a real clip named "spray_hiss" dropped into a Resources folder
        // (same convention as roach_hiss); otherwise a procedurally generated
        // aerosol hiss is used so there is always a sound.
        AudioClip sprayClip = Resources.Load<AudioClip>("spray_hiss");
        if (sprayClip == null) sprayClip = BuildSprayHiss();
        sprayAudio = gameObject.AddComponent<AudioSource>();
        sprayAudio.clip = sprayClip;
        sprayAudio.loop = true;
        sprayAudio.playOnAwake = false;
        sprayAudio.spatialBlend = 1f;               // 3D — comes from the can
        sprayAudio.rolloffMode = AudioRolloffMode.Linear;
        sprayAudio.minDistance = 0.2f;
        sprayAudio.maxDistance = 4f;
        sprayAudio.volume = 0.6f;

        // Refill the can whenever a new game starts (including restarts)
        if (InfestGameManager.Instance != null)
        {
            InfestGameManager.Instance.OnGameStarted.AddListener(ResetSpray);
        }

        Debug.Log($"[SprayCan] Ready. Controller={activeController}, Parent={transform.parent?.name ?? "ROOT"}");
    }

    void Update()
    {
        // Wait one frame so the transform hierarchy has fully settled
        // before allowing spray. Prevents world-space particles from
        // spawning at a stale position on the very first frame.
        if (!warmupDone)
        {
            warmupDone = true;
            return;
        }

        // Update controller assignment if it changes
        if (HandAssignmentManager.Instance != null)
        {
            activeController = HandAssignmentManager.Instance.SprayController;
        }

        // Stay anchored to the CONTROLLER, not the hand: when both controllers
        // are set down the headset switches to hand tracking and the hand
        // anchors follow the player's bare hands — the can would glue itself
        // to their hand. Detach and freeze at the controller's resting pose
        // instead; re-attach on pickup.
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

        // Fake occlusion by real furniture: when a furniture volume or wall
        // blocks the player's view of the controller (e.g. it was dropped
        // behind the sofa), the whole can disappears — you can't see the real
        // controller, so you can't see the virtual tool either.
        occlusionTimer -= Time.deltaTime;
        if (occlusionTimer <= 0f)
        {
            occlusionTimer = 0.1f;
            if (occlusionCam == null) occlusionCam = Camera.main;
            bool blocked = occlusionCam != null &&
                FurnitureCover.IsSegmentBlocked(occlusionCam.transform.position, transform.position);
            if (blocked != toolOccluded)
            {
                toolOccluded = blocked;
                // Fresh fetch on every toggle: the gauge builds its quads
                // lazily, so a list cached at Start could miss them
                foreach (var r in GetComponentsInChildren<Renderer>(true))
                    if (r != null) r.enabled = !blocked;
            }
        }

        // Index trigger = spray
        if (sprayParticles != null)
        {
            // Use Raw inputs for maximum reliability, falling back to mapped input
            float triggerValue = 0f;
            if (activeController == OVRInput.Controller.LTouch)
            {
                triggerValue = OVRInput.Get(OVRInput.RawAxis1D.LIndexTrigger);
            }
            else if (activeController == OVRInput.Controller.RTouch)
            {
                triggerValue = OVRInput.Get(OVRInput.RawAxis1D.RIndexTrigger);
            }

            // Fallback just in case
            if (triggerValue <= 0.01f)
            {
                triggerValue = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, activeController);
            }

            // An occluded (invisible) can doesn't spray — no particles from
            // behind the sofa
            bool wantSpray = triggerValue > sprayThreshold && !toolOccluded;

            // ── Spray budget: fixed pool for the whole round, no refill ──────
            bool spray = wantSpray && sprayAmount > 0f;

            if (spray)
            {
                sprayAmount -= Time.deltaTime / Mathf.Max(1f, effectiveSpraySeconds);
                if (sprayAmount <= 0f)
                {
                    sprayAmount = 0f;
                    spray = false;

                    // An empty can ends the round — no spray, no kills
                    if (InfestGameManager.Instance != null)
                        InfestGameManager.Instance.NotifySprayEmpty();
                }
            }

            if (gauge != null)
            {
                gauge.SetFill(sprayAmount);
            }

            // The most reliable way to toggle a continuous particle system in Unity
            // is to leave it "Playing" but toggle the Emission module on/off.
            var emission = sprayParticles.emission;
            if (emission.enabled != spray)
            {
                emission.enabled = spray;
            }

            // Ensure the system is actually playing so the emission toggle works
            if (spray && !sprayParticles.isPlaying)
            {
                sprayParticles.Play(true);
            }

            // Aerosol hiss: loop while spraying, cut the instant it stops
            // (release, empty can, or occluded — all fold into 'spray')
            if (sprayAudio != null)
            {
                if (spray && !sprayAudio.isPlaying) sprayAudio.Play();
                else if (!spray && sprayAudio.isPlaying) sprayAudio.Stop();
            }

            // Aerosol rumble on the spray hand only. SetControllerVibration
            // times out after ~2 seconds, so refresh it every frame while
            // spraying; send a single stop when the spray cuts out.
            if (spray)
            {
                OVRInput.SetControllerVibration(1f, sprayVibration, activeController);
                vibrating = true;
            }
            else if (vibrating)
            {
                OVRInput.SetControllerVibration(0f, 0f, activeController);
                vibrating = false;
            }
        }
    }

    void OnDisable()
    {
        // Never leave a controller buzzing (scene reload, object teardown)
        if (vibrating)
        {
            OVRInput.SetControllerVibration(0f, 0f, activeController);
            vibrating = false;
        }
    }

    /// <summary>
    /// Refills the spray budget — called when a new game starts. Longer games
    /// grant a bigger budget (+30s per minute of game length above baseline).
    /// </summary>
    public void ResetSpray()
    {
        effectiveSpraySeconds = totalSpraySeconds;
        if (InfestGameManager.Instance != null)
            effectiveSpraySeconds += InfestGameManager.Instance.ResourceBonusSeconds;

        sprayAmount = 1f;
        if (gauge != null) gauge.SetFill(1f);

        Debug.Log($"[SprayCan] Budget for this round: {effectiveSpraySeconds}s");
    }

    // Kill detection lives in SprayParticleKillRelay, attached to the
    // particle system's GameObject in Start() — Unity only delivers
    // OnParticleCollision there, not to this controller.

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Removes all Grabbable (Oculus Interaction SDK), Rigidbody, and Collider
    /// components from a GameObject so it can be permanently parented to a hand.
    /// </summary>
    private void RemoveGrabComponents(GameObject target)
    {
        // 1. Disable and destroy all Oculus Interaction SDK MonoBehaviours
        //    (Grabbable, PointableUnityEventWrapper, PhysicsGrabbable, etc.)
        foreach (var mb in target.GetComponents<MonoBehaviour>())
        {
            if (mb == null) continue;
            string ns = mb.GetType().Namespace ?? "";
            if (ns.StartsWith("Oculus.Interaction") || ns.StartsWith("Meta.XR"))
            {
                mb.enabled = false;
                Destroy(mb);
            }
        }

        // 2. Remove Rigidbody (must go after MonoBehaviours that reference it)
        var rb = target.GetComponent<Rigidbody>();
        if (rb != null)
        {
            Debug.Log($"[SprayCan] Removing Rigidbody from {target.name}");
            Destroy(rb);
        }

        // 3. Remove grab-detection colliders
        foreach (var col in target.GetComponents<Collider>())
        {
            Debug.Log($"[SprayCan] Removing {col.GetType().Name} from {target.name}");
            Destroy(col);
        }
    }

    /// <summary>
    /// Builds a 1-second looping aerosol "hiss" from lightly low-passed white
    /// noise, so the spray always has a sound even with no audio asset in the
    /// project. Noise has no period, so the loop seam is inaudible. Drop a real
    /// clip named "spray_hiss" into a Resources folder to override it (see Start).
    /// </summary>
    private static AudioClip BuildSprayHiss()
    {
        const int sampleRate = 44100;
        const int length = sampleRate; // 1 second

        var data = new float[length];
        var rng = new System.Random(8731);
        float prev = 0f;
        for (int i = 0; i < length; i++)
        {
            float white = (float)(rng.NextDouble() * 2.0 - 1.0);
            // Mild low-pass takes the harsh edge off pure white noise so it
            // reads as pressurised spray rather than TV static.
            prev = Mathf.Lerp(prev, white, 0.6f);
            data[i] = prev * 0.7f;
        }

        var clip = AudioClip.Create("SprayHiss", length, 1, sampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }
}
