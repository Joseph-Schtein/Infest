using UnityEngine;
using Pathfinding;

/// <summary>
/// Auto-configures the cockroach prefab instance at runtime (v2).
/// Adds all required components if missing: Rigidbody (kinematic),
/// CapsuleCollider (solid, for spray hits), Seeker, CockroachMotor,
/// CockroachBehavior, AudioSources.
///
/// v2 note: movement is CockroachMotor + graph-grown paths — there is no
/// AIPath anymore. Any AIPath left on the prefab from v1 is removed so it
/// can't fight the motor over the transform. ALL movement tuning lives on
/// CockroachBehavior/CockroachMotor (v1 split it across two files with
/// conflicting values — this file no longer sets any movement numbers).
///
/// Attach this to the roach prefab.
/// </summary>
public class CockroachSetup : MonoBehaviour
{
    [Header("Collider Settings")]
    [Tooltip("Collider radius for the cockroach")]
    public float colliderRadius = 0.03f;

    [Tooltip("Collider height for the cockroach")]
    public float colliderHeight = 0.06f;

    void Awake()
    {
        ConfigureComponents();
    }

    private void ConfigureComponents()
    {
        // ─── Strip v1 leftovers ─────────────────────────────────────────────
        // AIPath (and a destination setter, if any) would fight the motor
        // for the transform every frame — remove them before anything runs
        foreach (var stale in GetComponents<AIPath>())
        {
            Debug.Log($"[CockroachSetup] Removing v1 AIPath from {gameObject.name}");
            Destroy(stale);
        }
        foreach (var stale in GetComponents<AIDestinationSetter>())
        {
            Debug.Log($"[CockroachSetup] Removing v1 AIDestinationSetter from {gameObject.name}");
            Destroy(stale);
        }

        // ─── Rigidbody (kinematic, so spray particle collisions register) ───
        var rb = GetComponent<Rigidbody>();
        if (rb == null)
        {
            rb = gameObject.AddComponent<Rigidbody>();
        }
        rb.isKinematic = true;
        rb.useGravity = false;

        // ─── Collider (solid, for spray particle detection) ─────────────────
        // Must NOT be a trigger: the particle system's Collision module
        // ignores trigger colliders, so spray hits would never register.
        // The kinematic rigidbody keeps it from being pushed around.

        // Strip any collider that isn't the body capsule — an oversized
        // collider here means the spray kills roaches from half a meter away
        foreach (var stray in GetComponents<Collider>())
        {
            if (!(stray is CapsuleCollider))
            {
                Debug.Log($"[CockroachSetup] Removing stray {stray.GetType().Name} from {gameObject.name}");
                Destroy(stray);
            }
        }

        var col = GetComponent<CapsuleCollider>();
        if (col == null)
        {
            col = gameObject.AddComponent<CapsuleCollider>();
        }
        col.isTrigger = false;
        col.radius = colliderRadius;
        col.height = colliderHeight;
        col.direction = 2; // Z-axis (forward) for the cockroach body
        col.center = new Vector3(0f, colliderRadius, 0f);

        // ─── Seeker (computes FleePath/RandomPath/ABPath for the brain) ─────
        var seeker = GetComponent<Seeker>();
        if (seeker == null)
        {
            seeker = gameObject.AddComponent<Seeker>();
        }

        // ─── Motor + Brain ──────────────────────────────────────────────────
        // Order matters only for clarity — CockroachBehavior's
        // RequireComponent attributes would auto-add both anyway
        var motor = GetComponent<CockroachMotor>();
        if (motor == null)
        {
            motor = gameObject.AddComponent<CockroachMotor>();
        }

        var behavior = GetComponent<CockroachBehavior>();
        if (behavior == null)
        {
            behavior = gameObject.AddComponent<CockroachBehavior>();
        }

        // ─── Audio Sources (scurry/hiss + death squish) ─────────────────────
        var audioSources = GetComponents<AudioSource>();
        AudioSource scurrySource = null;
        AudioSource deathSource = null;

        if (audioSources.Length >= 2)
        {
            scurrySource = audioSources[0];
            deathSource = audioSources[1];
        }
        else
        {
            if (audioSources.Length == 0)
            {
                scurrySource = gameObject.AddComponent<AudioSource>();
                deathSource = gameObject.AddComponent<AudioSource>();
            }
            else // Length == 1
            {
                scurrySource = audioSources[0];
                deathSource = gameObject.AddComponent<AudioSource>();
            }
        }

        // Linear rolloff: the default logarithmic curve with a tiny
        // minDistance drops to ~10% volume at 1m — hisses were inaudible.
        scurrySource.playOnAwake = false;
        scurrySource.loop = false;
        scurrySource.spatialBlend = 1f; // 3D sound
        scurrySource.rolloffMode = AudioRolloffMode.Linear;
        scurrySource.maxDistance = 3f;
        scurrySource.minDistance = 0.3f;
        scurrySource.volume = 0.9f;

        // The prefab's source may have Play On Awake with a clip assigned,
        // started before the flags above were applied — without this every
        // spawned roach announces itself with a sound
        scurrySource.Stop();

        deathSource.playOnAwake = false;
        deathSource.loop = false;
        deathSource.spatialBlend = 1f;
        deathSource.rolloffMode = AudioRolloffMode.Linear;
        deathSource.maxDistance = 5f;
        deathSource.minDistance = 0.3f;
        deathSource.volume = 0.8f;

        // Assign to behavior
        behavior.scurryAudio = scurrySource;
        behavior.deathAudio = deathSource;

        // ─── Animator check ─────────────────────────────────────────────────
        var anim = GetComponentInChildren<Animator>();
        if (anim != null)
        {
            anim.enabled = true;
        }

        // ─── Layer + tag for detection ──────────────────────────────────────
        gameObject.tag = "Cockroach";

        int cockroachLayer = LayerMask.NameToLayer("Cockroach");
        if (cockroachLayer != -1)
        {
            gameObject.layer = cockroachLayer;
        }
        else
        {
            gameObject.layer = 7; // Fallback to FlashlightController default
        }

        Debug.Log($"[CockroachSetup] Configured components on {gameObject.name}");
    }
}
