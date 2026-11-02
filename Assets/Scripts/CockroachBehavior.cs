using System.Collections.Generic;
using UnityEngine;
using Pathfinding;
using Meta.XR.MRUtilityKit;

/// <summary>
/// Cockroach AI, rebuilt (v2). Two components in this file:
///
///   CockroachBehavior — the BRAIN. A small state machine:
///       Idle → Startled → Fleeing → Vanishing → Hiding → Dead
///     Every route it produces is grown on the A* graph (Pro FleePath /
///     RandomPath / ABPath), so destinations are reachable BY CONSTRUCTION —
///     no guess-a-point-and-validate, which is what made v1 roaches orbit
///     furniture. When no graph exists, it falls back to a hand-built
///     straight-line route through the same motor pipeline.
///     Hiding is ALARMED (2026-07-19): a pressured hidden roach relocates
///     hide-to-hide — first to the nearest prism/wall cover, then (if the
///     player follows) to random hide spots — instead of bolting into the
///     open. Vanish spots prefer real occlusion over open floor the player
///     merely isn't facing (which includes behind their back).
///
///   CockroachMotor — the LEGS. A dumb follower of List&lt;Vector3&gt; routes
///     with insect-style steering: instant acceleration, hard stops,
///     pivot-in-place before sharp turns, per-roach Perlin wobble. It owns
///     the transform completely (no AIPath), y is locked to the floor, and
///     each step is vetoed against room bounds / furniture prisms.
///
/// External contract (unchanged from v1): FlashlightController calls
/// OnLightHit(Vector3); SprayParticleKillRelay calls TakeDamage(float);
/// CockroachSetup assigns scurryAudio / deathAudio.
/// </summary>
[RequireComponent(typeof(Seeker))]
[RequireComponent(typeof(CockroachMotor))]
public class CockroachBehavior : MonoBehaviour
{
    // ─── State Machine ──────────────────────────────────────────────────────────
    public enum CockroachState
    {
        Idle,       // fidget + short darts, unaware of the player
        Startled,   // frozen for a beat — "detected!" reaction time
        Fleeing,    // panic run away from the threat (FleePath)
        Vanishing,  // pressure off — committed run to an unseen spot (ABPath)
        Hiding,     // parked at the unseen spot, waiting
        Dead
    }

    [Header("State")]
    [SerializeField] private CockroachState currentState = CockroachState.Idle;
    public CockroachState CurrentState => currentState;

    // ─── Movement Settings ──────────────────────────────────────────────────────
    [Header("Movement")]
    [Tooltip("Base flee speed (randomized per roach)")]
    public float fleeSpeedMin = 0.5f;
    public float fleeSpeedMax = 1.0f;

    [Tooltip("Speed of the short idle darts")]
    public float idleDartSpeed = 0.22f;

    [Tooltip("Idle roaches dart to nearby spots so they look alive")]
    public bool idleWander = true;

    [Tooltip("How far a single idle dart can take the roach")]
    public float idleWanderRadius = 0.35f;

    [Tooltip("Flee run length — randomized between min and max on every flee")]
    public float fleeDistanceMin = 2.5f;
    public float fleeDistanceMax = 4.0f;

    [Tooltip("How long the panic lasts after the light leaves (seconds)")]
    public float fleeLingerTime = 1.5f;

    [Tooltip("Turn rate in degrees per second (roaches pivot fast)")]
    public float rotationSpeed = 900f;

    [Tooltip("If the A* graph is missing, run straight-line routes instead " +
             "so the roach always reacts to the light")]
    public bool directMovementFallback = true;

    // ─── Edge Affinity (thigmotaxis) ────────────────────────────────────────────
    [Header("Edge Affinity")]
    [Tooltip("Roaches hug walls and furniture bases and never rest out on " +
             "open floor. Turn OFF to restore the old open-floor idling")]
    public bool edgeAffinity = true;

    [Tooltip("How far a roach will look for a wall/furniture edge to hug (m)")]
    public float edgeSeekRadius = 2.5f;

    [Tooltip("Gap between the wall/furniture surface and the resting roach (m)")]
    public float edgeMargin = 0.1f;

    [Tooltip("A roach already this close to an edge counts as tucked in and " +
             "stays put (m)")]
    public float edgeSettleTolerance = 0.25f;

    [Tooltip("Speed of the crawl to an edge — a touch brisker than an " +
             "aimless idle dart, because it's a purposeful move")]
    public float edgeSeekSpeed = 0.35f;

    // ─── Cover Dash ─────────────────────────────────────────────────────────────
    [Header("Cover Dash")]
    [Tooltip("A spooked roach with safe cover right next to it ducks behind " +
             "the furniture instead of sprinting across open floor")]
    public bool panicCoverDash = true;

    [Tooltip("How far (m) a spooked roach will go to reach cover. This is the " +
             "difference between breaking your line of sight and sprinting " +
             "across open floor: at 1.5m the dash almost never fired in a real " +
             "room, because furniture is usually further away than that")]
    public float panicCoverRange = 3.5f;

    // ─── Player Avoidance ───────────────────────────────────────────────────────
    [Header("Player Avoidance")]
    [Tooltip("Roaches never ROUTE within this horizontal distance (m) of the " +
             "player unless they are trapped — only then may they dart " +
             "near/under the player (the deciding test is whether the path " +
             "MUST pass near them, not where it ends). Set 0 to disable.")]
    public float playerAvoidRadius = 1.5f;

    [Tooltip("How expensive the ground at the player's feet is for the " +
             "pathfinder (extra cost multiplier at the bubble center, fading " +
             "linearly to normal at playerAvoidRadius). Paths curve around " +
             "the player whenever a detour exists and pass near/under only " +
             "when nothing else is affordable. MUST stay well above the " +
             "baker's openFloorPenalty everywhere inside the radius — at 12 " +
             "with a quadratic ramp, sneaking along a wall PAST the player " +
             "(1.05-2.9x near the rim) was cheaper than crossing open floor " +
             "(3x), and the pathfinder rationally chose to skim the player. " +
             "0 = off.")]
    public float playerAvoidCost = 40f;

    // ─── Startle ────────────────────────────────────────────────────────────────
    [Header("Startle")]
    [Tooltip("Freeze duration when first spooked — reads as insect reaction time")]
    public float startleTimeMin = 0.06f;
    public float startleTimeMax = 0.22f;

    // ─── Hiding ─────────────────────────────────────────────────────────────────
    [Header("Hiding")]
    [Tooltip("If the player gets this close to a hiding roach AND can see it, it bolts")]
    public float flushDistance = 1.0f;

    [Tooltip("A roach bolts when the player gets this close (HORIZONTAL " +
             "distance — the head is always ~1.5m above the floor)")]
    public float playerScareDistance = 2.0f;

    [Tooltip("A pressured hidden roach remembers the chase this long (s): " +
             "the FIRST shove sends it to the nearest prism/wall cover, any " +
             "further shove within this window sends it to a RANDOM hide " +
             "spot instead, so a following player can't corner it")]
    public float hideChaseMemory = 6f;

    [Tooltip("A pressured roach leaving one hiding place must gain at least " +
             "this much ground (m). Without it the 'nearest cover' search " +
             "returns the spot the roach is ALREADY sitting on, it shuffles " +
             "a few centimetres, and it reads as ignoring you completely")]
    public float minRelocateDistance = 0.9f;

    [Tooltip("A HIDING roach starts relocating when an APPROACHING player " +
             "reaches this distance (m, horizontal). Must be larger than " +
             "playerScareDistance: a roach moves at ~0.75 m/s and needs ~3s " +
             "to reach new cover, while a walking player crosses 2m in under " +
             "2s — reacting at playerScareDistance means it is always caught " +
             "mid-run. Set equal to playerScareDistance to disable this rule")]
    public float hideAlertDistance = 4f;

    // ─── Health ─────────────────────────────────────────────────────────────────
    [Header("Health")]
    [Tooltip("Hit points. Default 1 = one spray hit kills")]
    public float maxHP = 1f;
    private float currentHP;

    // ─── Death ──────────────────────────────────────────────────────────────────
    [Header("Death")]
    [Tooltip("Time to wait before destroying the dead cockroach")]
    public float deathDestroyDelay = 3f;

    [Tooltip("How fast the death flip animation plays")]
    public float deathFlipSpeed = 5f;

    [Tooltip("When a roach dies, living roaches within this HORIZONTAL " +
             "distance (m) are alarmed — the swarm notices the kill and " +
             "scatters instead of waiting its turn. 0 disables")]
    public float deathAlarmRadius = 1.2f;

    // ─── Audio ──────────────────────────────────────────────────────────────────
    [Header("Audio (Assign clips later)")]
    public AudioSource scurryAudio;
    public AudioSource deathAudio;

    [Tooltip("One-shot hiss when the player gets close. Auto-loads Resources/roach_hiss if left empty")]
    public AudioClip hissClip;

    [Tooltip("Player distance that counts as 'passing near' this roach")]
    public float hissRange = 1f;

    [Tooltip("One hiss plays for every this-many roaches the player passes near")]
    public int roachesPerHiss = 25;

    // ─── Grounding ────────────────────────────────────────────────────────────
    [Header("Grounding")]
    [Tooltip("Lowers the visible model this far (m) so its FEET rest on the " +
             "floor. Two stacked causes, both measured: the spawner lifts each " +
             "roach ~5mm above the floor (locked for life by the motor), AND " +
             "the SkinnedMeshRenderer's padded bounds sit ~11.7mm BELOW the " +
             "actual feet — so grounding the bounds bottom floats the feet by " +
             "that much. 0.018 = 5mm lift + 11.7mm bounds gap + ~1mm bias into " +
             "the (invisible) passthrough floor. Raise if still floating, lower " +
             "if legs sink visibly.")]
    public float groundClearance = 0.025f;

    // ─── Animation ──────────────────────────────────────────────────────────────
    [Header("Animation")]
    public float idleAnimSpeed = 0.1f;
    public float fleeAnimSpeed = 1.5f;

    // ─── Internal References ────────────────────────────────────────────────────
    private Seeker seeker;
    private CockroachMotor motor;
    private Animator animator;
    private Renderer[] renderers;
    private static Transform cachedCameraTransform;

    // Per-roach randomization
    private float actualFleeSpeed;
    private float actualFleeDistance;

    // Threat memory
    private Vector3 lastLightPosition;
    private float lastLightHitTime = -999f;

    // Committed flee heading for the current run (fanned away-direction)
    private Vector3 fleeDir = Vector3.forward;

    // Startle
    private float startleTimer;

    // Vanishing
    private Vector3 vanishSpot;
    private Vector3 vanishAnchor;   // where the player stood when the vanish began
    private int vanishTries;
    private bool vanishPreferCover = true; // prisms/walls first; open-floor cone spots only as fallback
    private bool alarmedRelocation;        // committed hide-to-hide run — pressure won't break it mid-run
    private float vanishMinMove;           // ground this vanish must actually gain (see minRelocateDistance)

    // A vanish burns one try per rejected spot (unreachable, or a route that
    // would sweep past the player). In a small room plenty get rejected, so
    // the budget has to be generous — a roach that runs out stops trying to
    // hide at all, which is how they ended up idling in the open.
    private const int MaxVanishTries = 8;

    // Scratch pool for TryFindVanishSpot. Shared and reused because that
    // method runs synchronously on the main thread and a spooked group can
    // vanish in the same frame — a fresh List per call per roach is pure
    // garbage on Quest.
    private static readonly List<Vector3> vanishCandidates = new List<Vector3>();
    private static readonly List<Vector3> panicCandidates = new List<Vector3>();

    // Alarmed hiding: how many times the player has shoved this roach out of hiding
    private int hidePressureStrikes;
    private float lastHidePressureTime = -999f;

    // Approach detection for hideAlertDistance. The player's distance is
    // compared against a HELD reference sample rather than the previous tick:
    // the HMD jitters a couple of centimetres even when the player stands
    // still, which at a 0.15s tick reads as a steady approach and would flush
    // every hidden roach in the room. Over this longer window a deliberate
    // walk clears the margin easily and jitter never does.
    private const float HideApproachWindow = 0.5f;  // s between reference samples
    private const float HideApproachMargin = 0.15f; // m closed to count (~0.3 m/s)
    private float hideApproachRef;
    private float hideApproachRefTime = -1f;

    // Panic cover dash (Fleeing): route to a claimed nearby cover spot
    private bool dashToCover;
    private Vector3 dashSpot;

    // Path request bookkeeping — a stale callback (from a cancelled or
    // superseded request) must never drive the motor
    private enum RequestKind { None, Flee, Breakout, Vanish, Wander, Dart, CoverDash }
    private RequestKind requestKind = RequestKind.None;
    private int requestId;
    private float lastRequestTime;

    // Circle watchdog (Fleeing only): covering ground without displacement
    // means the current escape is looping — re-plan, then cower if it repeats
    private float circleWindowTimer;
    private float circleTravelled;
    private Vector3 circleAnchor;
    private Vector3 circlePrevPos;
    private int circleStrikes;
    private const float CircleWindow = 1.6f;
    private const float CircleMinTravel = 0.55f;
    private const float CircleMaxDisplacement = 0.35f;

    // Each roach claims its vanish spot so a spooked group spreads over many
    // hiding places instead of piling behind the same couch
    private static readonly Dictionary<CockroachBehavior, Vector3> claimedSpots =
        new Dictionary<CockroachBehavior, Vector3>();
    private const float ClaimSeparation = 0.4f;

    // Every living roach, for swarm-wide signals (currently the death alarm)
    private static readonly List<CockroachBehavior> allRoaches =
        new List<CockroachBehavior>();

    // Idle
    private float idleFidgetTimer;
    private float idleDartTimer;

    // Death
    private bool isDead;
    private float deathTimer;
    private Quaternion deathStartRotation;
    private Quaternion targetDeathRotation;

    // Visibility (fake occlusion by real furniture)
    private bool renderersVisible = true;
    private float visCheckTimer;

    // (Grounding is measured and applied fresh every frame in
    // AlignModelToGround — a cached-offset version caused the model to
    // accumulate sink between measurements and visibly flicker.)

    // Hiss
    private float hissCheckTimer;
    private bool countedNearPlayer;
    private bool hasHissed;
    private static int roachesPassedNearPlayer;
    private static bool firstHissDone;

    // ─── Lifecycle ──────────────────────────────────────────────────────────────

    void Awake()
    {
        currentHP = maxHP;
        allRoaches.Add(this);

        seeker = GetComponent<Seeker>();
        motor = GetComponent<CockroachMotor>();
        animator = GetComponentInChildren<Animator>();
        renderers = GetComponentsInChildren<Renderer>(true);

        if (hissClip == null)
            hissClip = Resources.Load<AudioClip>("roach_hiss");

        // Per-roach variation so the swarm never moves in lockstep
        actualFleeSpeed = Random.Range(fleeSpeedMin, fleeSpeedMax);
        actualFleeDistance = Random.Range(fleeDistanceMin, fleeDistanceMax);

        motor.rotationSpeed = rotationSpeed;
        motor.wobbleAmplitude = Random.Range(0.02f, 0.045f);
        motor.wobbleFrequency = Random.Range(4f, 8f);
        motor.SetFloorY(transform.position.y);
        motor.Arrived += OnMotorArrived;
        motor.Blocked += OnMotorBlocked;

        idleFidgetTimer = Random.Range(1f, 3f);
        idleDartTimer = Random.Range(1f, 3f);

        // Stagger periodic checks so a group doesn't test on the same frame
        visCheckTimer = Random.value * 0.1f;
        hissCheckTimer = Random.value * 0.3f;
    }

    void Start()
    {
        // CockroachSetup wires the audio sources AFTER Awake (it adds this
        // component first, then configures audio) — bind the hiss fallback late
        if (hissClip == null && scurryAudio != null)
            hissClip = scurryAudio.clip;
    }

    void Update()
    {
        // Occlusion by real furniture applies in every state (even Dead —
        // a corpse behind the couch shouldn't float visibly through it)
        UpdateVisibility();

        if (!isDead)
        {
            hissCheckTimer -= Time.deltaTime;
            if (hissCheckTimer <= 0f)
            {
                hissCheckTimer = Random.Range(0.25f, 0.45f); // desync roaches
                TryHiss();
            }
        }

        switch (currentState)
        {
            case CockroachState.Idle: UpdateIdle(); break;
            case CockroachState.Startled: UpdateStartled(); break;
            case CockroachState.Fleeing: UpdateFleeing(); break;
            case CockroachState.Vanishing: UpdateVanishing(); break;
            case CockroachState.Hiding: UpdateHiding(); break;
            case CockroachState.Dead: UpdateDead(); break;
        }

        UpdateAnimator();
    }

    void LateUpdate()
    {
        // Re-ground the model every frame (post-Animator). Skipped while
        // dead — the death flip rotates the body around the floor pivot and
        // grounding would fight it — and while hidden by the fake-occlusion
        // fallback, because disabled renderers report stale bounds.
        // NOTE: this must measure FRESH each frame (the v1-proven approach).
        // A cached-offset version accumulated sink between measurements and
        // made the roaches visibly flicker/bounce.
        if (!isDead && renderersVisible)
            AlignModelToGround();
    }

    /// <summary>
    /// The imported roach model's pivot doesn't sit exactly at its feet,
    /// which leaves the roach hovering. Shift the child model so the bottom
    /// of its renderer bounds rests exactly at this transform's Y (the
    /// floor). Runs post-Animator because the walk/idle clips re-pose the
    /// model every frame — a one-time alignment doesn't stick.
    /// </summary>
    private void AlignModelToGround()
    {
        if (renderers == null || renderers.Length == 0) return;

        bool hasBounds = false;
        Bounds combined = default;
        foreach (var r in renderers)
        {
            if (r == null) continue;
            if (!hasBounds) { combined = r.bounds; hasBounds = true; }
            else combined.Encapsulate(r.bounds);
        }
        if (!hasBounds) return;

        // Ground to floorY, NOT to transform.position.y: the transform sits
        // floorOffset above the true floor (spawn lift, locked by the motor),
        // so grounding to it reproduced that lift as a permanent hover.
        // groundClearance sinks the model back down onto the floor.
        float sink = combined.min.y - (transform.position.y - groundClearance);
        if (Mathf.Abs(sink) < 0.005f) return;

        foreach (Transform child in transform)
            child.position -= new Vector3(0f, sink, 0f);
    }

    // ─── State: Idle ────────────────────────────────────────────────────────────

    private void UpdateIdle()
    {
        // Player stepping close spooks the roach just like the flashlight
        if (IsPlayerTooClose())
        {
            EnterStartled();
            return;
        }

        if (motor.IsMoving) return; // mid-dart — let it finish

        // Small random fidgets between darts
        idleFidgetTimer -= Time.deltaTime;
        if (idleFidgetTimer <= 0f)
        {
            idleFidgetTimer = Random.Range(2f, 4f);
            transform.Rotate(0f, Random.Range(-45f, 45f), 0f);
        }

        // Insect gait: short dart, then a long pause — not a constant crawl
        if (!idleWander) return;
        idleDartTimer -= Time.deltaTime;
        if (idleDartTimer <= 0f)
        {
            idleDartTimer = Random.Range(1.5f, 4f);
            RequestIdleDart();
        }
    }

    private void RequestIdleDart()
    {
        // Thigmotaxis (Yossi 2026-07-19): a roach caught out on open floor
        // crawls to the nearest wall or furniture base instead of milling
        // around mid-room being an easy target. Only fires when it isn't
        // already tucked against something.
        if (edgeAffinity && TryFindEdgeRetreat(out Vector3 edge))
        {
            motor.maxSpeed = edgeSeekSpeed;
            if (GraphAvailable())
                StartRequest(ABPath.Construct(transform.position, edge), RequestKind.Dart);
            else
                motor.FollowPath(new List<Vector3> { transform.position, edge });
            return;
        }

        motor.maxSpeed = idleDartSpeed;

        if (GraphAvailable())
        {
            // Graph-grown mini-run: always lands somewhere walkable
            int length = Mathf.RoundToInt(Random.Range(0.12f, idleWanderRadius) * 1000f);
            var p = RandomPath.Construct(transform.position, length);
            StartRequest(p, RequestKind.Dart);
            return;
        }

        // No graph: straight two-point dart to a valid nearby spot
        for (int i = 0; i < 4; i++)
        {
            Vector2 rnd = Random.insideUnitCircle.normalized * Random.Range(0.1f, idleWanderRadius);
            Vector3 candidate = transform.position + new Vector3(rnd.x, 0f, rnd.y);
            if (IsValidPoint(candidate))
            {
                motor.FollowPath(new List<Vector3> { transform.position, candidate });
                return;
            }
        }
    }

    /// <summary>
    /// The nearest wall/furniture edge worth crawling to. Returns false when
    /// the roach is already hugging something (nothing to do), when the spot
    /// is taken or invalid, or when it sits in the player's space.
    /// </summary>
    private bool TryFindEdgeRetreat(out Vector3 edge)
    {
        if (!FurnitureCover.TryFindEdgeSpot(
                transform.position, edgeSeekRadius, edgeMargin, out edge))
            return false;

        // Already tucked in — stay put
        if (HorizontalDistance(edge, transform.position) < edgeSettleTolerance) return false;

        if (!IsValidPoint(edge)) return false;
        if (IsNearClaimedSpot(edge)) return false;

        Transform cam = GetCameraTransform();
        if (cam != null && TooCloseToPlayer(edge, cam.position)) return false;

        return true;
    }

    // ─── State: Startled ────────────────────────────────────────────────────────

    /// <summary>
    /// A beat of frozen "detected!" before the bolt — insect reaction time.
    /// Entered from Idle/Hiding/Vanishing whenever a threat appears.
    /// </summary>
    private void EnterStartled()
    {
        if (isDead) return;
        currentState = CockroachState.Startled;
        startleTimer = Random.Range(startleTimeMin, startleTimeMax);
        alarmedRelocation = false;
        motor.Stop();
        ReleaseClaim();
    }

    private void UpdateStartled()
    {
        startleTimer -= Time.deltaTime;
        if (startleTimer <= 0f)
            EnterFleeing();
    }

    // ─── State: Fleeing ─────────────────────────────────────────────────────────

    private void EnterFleeing()
    {
        if (isDead) return;
        currentState = CockroachState.Fleeing;

        // Fresh run length each flee so escapes don't look uniform
        actualFleeDistance = Random.Range(fleeDistanceMin, fleeDistanceMax);
        motor.maxSpeed = actualFleeSpeed;

        // Committed heading: away from the threat, fanned by a per-roach
        // random angle so a spooked GROUP opens up like a hand of cards
        fleeDir = AwayFromThreatDir();

        // Fresh circle-watchdog window
        circleWindowTimer = CircleWindow;
        circleTravelled = 0f;
        circleAnchor = transform.position;
        circlePrevPos = transform.position;
        circleStrikes = 0;
        dashToCover = false;

        // Cover shortcut (Yossi 2026-07-18): a roach with safe cover in
        // reach ducks behind the furniture instead of sprinting across open
        // floor. Never a toward-player run.
        if (TryStartCoverDash()) return;

        RequestFleeRun();
    }

    /// <summary>
    /// Dashes to nearby cover if any is available and safe. Returns false
    /// when the roach has nothing to duck behind and should take a plain
    /// panic run instead. Re-checked on every leg of a flee, not just at
    /// the start — a roach that has run somewhere new may have cover in
    /// reach now even though it had none when it was first spooked.
    /// </summary>
    private bool TryStartCoverDash()
    {
        if (!panicCoverDash || !TryFindPanicCover(out dashSpot)) return false;

        dashToCover = true;
        if (GraphAvailable())
            StartRequest(ABPath.Construct(transform.position, dashSpot), RequestKind.CoverDash);
        else
            motor.FollowPath(new List<Vector3> { transform.position, dashSpot });
        return true;
    }

    /// <summary>
    /// A safe nearby hiding spot for the panic dash: behind furniture
    /// relative to where the player NOW is (FurnitureCover verifies the
    /// occlusion), outside the scare radius (settling inside it would be
    /// instantly flushed), unclaimed, and never meaningfully TOWARD the
    /// player — sideways ducks are fine, approaches are not.
    /// </summary>
    private bool TryFindPanicCover(out Vector3 spot)
    {
        spot = default;
        Transform cam = GetCameraTransform();
        if (cam == null) return false;

        // Furniture cover (the far side of a prism) AND wall cover (corners
        // and alcoves in the room outline). This saw furniture ONLY until now,
        // so a roach with a perfectly good corner two metres away still fell
        // through to a plain FleePath — and FleePath maximises DISTANCE by
        // construction. That is exactly the "runs away instead of ducking
        // behind something" that shows up the instant a roach is spooked.
        panicCandidates.Clear();
        if (FurnitureCover.TryFindHidingSpot(
                transform.position, cam.position, panicCoverRange, out Vector3 furnitureSpot))
            panicCandidates.Add(furnitureSpot);

        FurnitureCover.CollectCornerSpots(
            transform.position, panicCoverRange, edgeMargin, panicCandidates);

        // Nearest valid one wins — this is a dash, not a considered
        // relocation, so closest cover beats a prettier one further away.
        float bestDist = float.MaxValue;
        bool found = false;

        for (int i = 0; i < panicCandidates.Count; i++)
        {
            Vector3 c = panicCandidates[i];

            if (!IsValidPoint(c)) continue;
            if (!IsOccludedFromPlayer(c)) continue;
            if (HorizontalDistance(c, cam.position) < playerScareDistance) continue;
            if (MovesTowardPlayer(c, cam.position)) continue;
            if (IsNearClaimedSpot(c)) continue;

            float d = HorizontalDistance(c, transform.position);
            if (d >= bestDist) continue;

            bestDist = d;
            spot = c;
            found = true;
        }

        if (!found) return false;

        claimedSpots[this] = spot;
        return true;
    }

    private void UpdateFleeing()
    {
        // A player standing on top of the roach keeps it running
        IsPlayerTooClose();

        UpdateCircleWatchdog();
        if (currentState != CockroachState.Fleeing) return;

        // Pressure off → IMMEDIATELY stop panicking and go vanish somewhere
        // unseen (the v1 device-approved flip — an extra linger here kept
        // roaches in pure-run mode so long they never seemed to hide)
        bool threatActive = IsLitRecently() || IsPlayerWithinScareDistance();
        if (!threatActive)
        {
            BeginVanish();
            return;
        }

        // Safety: if the motor is idle and no request is pending, re-plan
        // (covers a lost/failed path callback)
        if (!motor.IsMoving && requestKind == RequestKind.None
            && Time.time - lastRequestTime > 0.6f)
        {
            RequestFleeRun();
        }
    }

    /// <summary>
    /// Panic run (A* Pro FleePath): the pathfinder walks the graph outward
    /// from the roach until the run is actualFleeDistance long, preferring
    /// nodes that increase distance to the threat. Every step is walkable
    /// and connected by construction, so the run cannot target the
    /// unreachable. The default aim (the exact away-line, identical for
    /// every roach) is replaced with this flee's fanned direction so the
    /// group disperses.
    /// </summary>
    private void RequestFleeRun()
    {
        // A plain flee always abandons any pending cover dash
        dashToCover = false;
        ReleaseClaim();

        if (!GraphAvailable())
        {
            if (directMovementFallback) FallbackStraightRun();
            return;
        }

        // Flatten the threat to the floor plane — the player's head being
        // ~1.5m up would tilt FleePath's aim uselessly downward
        Vector3 threat = GetThreatPosition();
        threat.y = transform.position.y;

        // G-score costs are ~1000 per world unit travelled
        FleePath p = FleePath.Construct(
            transform.position, threat,
            Mathf.RoundToInt(actualFleeDistance * 1000f));
        p.spread = 1500;
        p.aimStrength = 1f;
        // 10x scale matches what FleePath.Setup uses for its own aim point
        p.aim = transform.position + fleeDir * (actualFleeDistance * 10f);

        StartRequest(p, RequestKind.Flee);
    }

    /// <summary>
    /// Corner breakout (A* Pro RandomPath): same graph-grown run but aimed
    /// along a re-rolled direction instead of away from the player — used
    /// when "keep running away" has jammed the roach into a corner.
    /// </summary>
    private void RequestBreakout(Vector3? forcedDir = null)
    {
        dashToCover = false;
        ReleaseClaim();
        fleeDir = forcedDir ?? Quaternion.Euler(0f, Random.Range(90f, 270f), 0f) * fleeDir;

        if (!GraphAvailable())
        {
            if (directMovementFallback) FallbackStraightRun();
            return;
        }

        RandomPath p = RandomPath.Construct(
            transform.position,
            Mathf.RoundToInt(actualFleeDistance * 1000f));
        p.spread = 1500;
        p.aimStrength = 0.7f; // pull toward the direction, not a hard rule
        p.aim = transform.position + fleeDir * actualFleeDistance;

        StartRequest(p, RequestKind.Breakout);
    }

    private void UpdateCircleWatchdog()
    {
        Vector3 step = transform.position - circlePrevPos;
        step.y = 0f;
        circleTravelled += step.magnitude;
        circlePrevPos = transform.position;

        circleWindowTimer -= Time.deltaTime;
        if (circleWindowTimer > 0f) return;

        Vector3 net = transform.position - circleAnchor;
        net.y = 0f;
        bool circling = circleTravelled >= CircleMinTravel
                     && net.magnitude < CircleMaxDisplacement;

        circleWindowTimer = CircleWindow;
        circleTravelled = 0f;
        circleAnchor = transform.position;

        if (!circling)
        {
            circleStrikes = 0;
            return;
        }

        circleStrikes++;
        Debug.Log($"[Cockroach] Circling (strike {circleStrikes}) — breaking out");

        if (circleStrikes >= 2)
        {
            circleStrikes = 0;
            dashToCover = false;
            ReleaseClaim();

            // Circling alone is not proof the PLAYER is the wall (device
            // feedback 2026-07-20: leg-darts fired when the roach was merely
            // jammed against furniture with the player nearby). The dart is
            // earned only when no clear ground lane away from the player
            // exists; with any open lane, break out along it instead.
            if (TryFindClearLane(out Vector3 lane))
            {
                RequestBreakout(lane);
                return;
            }

            // Genuinely boxed in: dart between the player's legs if an exit
            // exists there, otherwise settle down instead of looping forever
            if (TryDartUnderPlayer(out List<Vector3> dart))
            {
                Debug.Log($"[Cockroach:{GetInstanceID()}] DART taken (trigger=circling, playerDist={DebugPlayerDistance():F2}m)");
                motor.maxSpeed = actualFleeSpeed * 1.2f; // adrenaline burst
                motor.FollowPath(dart);
            }
            else
            {
                Debug.Log($"[Cockroach:{GetInstanceID()}] trapped (trigger=circling) but no dart exit — vanishing");
                BeginVanish();
            }
            return;
        }

        RequestBreakout();
    }

    /// <summary>
    /// Any open escape lane that avoids the player: 8 compass directions
    /// (jittered so repeated checks don't retrace), each valid at 0.5m and
    /// 1m out, with its near stretch keeping outside the player-avoidance
    /// radius. Vetoes the between-the-legs dart for roaches that are merely
    /// jammed against furniture rather than walled in by the player.
    /// </summary>
    private bool TryFindClearLane(out Vector3 dir)
    {
        Transform cam = GetCameraTransform();
        bool checkPlayer = PlayerAvoidanceOn && cam != null;
        Vector2 player = checkPlayer
            ? new Vector2(cam.position.x, cam.position.z)
            : default;

        for (int i = 0; i < 8; i++)
        {
            Vector3 d = Quaternion.Euler(0f, i * 45f + Random.Range(-10f, 10f), 0f) * Vector3.forward;
            Vector3 near = transform.position + d * 0.5f;
            Vector3 far = transform.position + d * 1f;
            near.y = transform.position.y;
            far.y = transform.position.y;

            if (!IsValidPoint(near) || !IsValidPoint(far)) continue;
            if (checkPlayer)
            {
                if ((new Vector2(near.x, near.z) - player).magnitude < playerAvoidRadius) continue;
                if ((new Vector2(far.x, far.z) - player).magnitude < playerAvoidRadius) continue;
            }

            dir = d;
            return true;
        }

        dir = Vector3.forward;
        return false;
    }

    /// <summary>
    /// Trapped-roach escape: dart straight through the legs of a close
    /// player to a valid exit on their far side. Deliberately a hand-built
    /// straight route — running under the player is the point.
    /// </summary>
    private bool TryDartUnderPlayer(out List<Vector3> route)
    {
        route = null;
        Transform cam = GetCameraTransform();
        if (cam == null) return false;

        Vector3 camFloor = new Vector3(cam.position.x, transform.position.y, cam.position.z);
        Vector3 toPlayer = camFloor - transform.position;
        toPlayer.y = 0f;
        if (toPlayer.magnitude >= playerScareDistance || toPlayer.sqrMagnitude <= 0.0001f)
            return false;

        Vector3 through = toPlayer.normalized;
        // Prefer a long exit past the player; shorter when a wall sits behind them
        float[] beyondOptions = { Random.Range(1.0f, 1.6f), 0.6f, 0.3f };
        foreach (float beyond in beyondOptions)
        {
            Vector3 exit = camFloor + through * beyond;
            if (IsValidPoint(exit))
            {
                Debug.Log($"[Cockroach:{GetInstanceID()}] dart exit {beyond:F2}m past player (roach was {toPlayer.magnitude:F2}m in front)");
                route = new List<Vector3> { transform.position, camFloor, exit };
                return true;
            }
        }
        return false;
    }

    // ─── State: Vanishing ───────────────────────────────────────────────────────

    /// <summary>
    /// The pressure is off — commit to a spot the player can't currently see
    /// and settle there so the roach won't be found again.
    /// </summary>
    private void BeginVanish(bool preferCover = true, bool alarmed = false)
    {
        if (isDead) return;
        currentState = CockroachState.Vanishing;
        motor.maxSpeed = actualFleeSpeed;
        vanishTries = 0;
        dashToCover = false;
        vanishPreferCover = preferCover;
        alarmedRelocation = alarmed;

        // Only a PRESSURED relocation has to gain ground. A roach that just
        // finished a flee run is somewhere new already — nearby cover is fine.
        vanishMinMove = alarmed ? minRelocateDistance : 0f;

        Transform cam = GetCameraTransform();
        vanishAnchor = cam != null ? cam.position : GetThreatPosition();

        TryNextVanishOption();
    }

    private void UpdateVanishing()
    {
        // Getting lit or crowded mid-vanish drops straight back into panic —
        // EXCEPT during an alarmed hide-to-hide relocation. That run was
        // TRIGGERED by the player pressing close, its route already avoids
        // them, and breaking it here would collapse straight back into the
        // open-floor bolt the alarmed design replaces. It commits; pressure
        // is re-evaluated the moment it arrives (UpdateHiding strikes again).
        if (!alarmedRelocation && (IsLitRecently() || IsPlayerTooClose()))
        {
            EnterStartled();
            return;
        }

        // Safety re-plan if a callback was lost
        if (!motor.IsMoving && requestKind == RequestKind.None
            && Time.time - lastRequestTime > 0.6f)
        {
            TryNextVanishOption();
        }
    }

    private void TryNextVanishOption()
    {
        vanishTries++;
        if (vanishTries > MaxVanishTries || !TryFindVanishSpot(out vanishSpot, vanishPreferCover))
        {
            // No reachable unseen spot exists. Under live pressure that
            // means the roach is cornered — break into the open panic run
            // (whose trapped fallback may dart near/under the player).
            // Otherwise: settle where we are if unseen, or just calm down.
            if (IsLitRecently() || IsPlayerWithinScareDistance())
            {
                EnterStartled();
            }
            else if (IsOccludedFromPlayer(transform.position))
            {
                EnterHiding();
            }
            else
            {
                // Nowhere unseen to go (bare room, player sees it all). Calm
                // down, but do NOT park in the middle of the floor — the edge
                // crawl is due immediately (this is what left roaches sitting
                // in the open as easy targets)
                EnterIdle();
                idleDartTimer = 0f;
            }
            return;
        }

        claimedSpots[this] = vanishSpot;

        if (GraphAvailable())
        {
            // Reachability is proven by the path itself: if the computed
            // route ends far from the requested spot, the spot was
            // unreachable and the callback tries the next option
            StartRequest(ABPath.Construct(transform.position, vanishSpot), RequestKind.Vanish);
        }
        else
        {
            motor.FollowPath(new List<Vector3> { transform.position, vanishSpot });
        }
    }

    /// <summary>
    /// A spot the player can't currently see. Candidates split into three
    /// tiers (Yossi 2026-07-19): OCCLUDED — a prism or wall physically
    /// blocks the player's line of sight — then EDGE-HUGGING — pressed
    /// against a wall or furniture base — then CONE-ONLY, open floor the
    /// player merely isn't facing (which includes behind their back). A
    /// roach with real cover available always takes it; slipping behind
    /// the player or running far into the open are fallbacks, not first
    /// choices. The escalated "random hide" (a chased roach) draws from
    /// both TUCKED tiers at once, never from open floor. The winner within
    /// a tier stays PURE RANDOM. Spots keep clear of the vanish anchor
    /// (where the player stood), the live player, and other roaches' claims.
    /// </summary>
    private bool TryFindVanishSpot(out Vector3 spot, bool preferCover)
    {
        Transform vanishCam = GetCameraTransform();
        Vector3 playerNow = vanishCam != null ? vanishCam.position : vanishAnchor;

        // First choice (cover-preferring vanishes only): behind a piece of
        // furniture, on the far side from the player — verified occluded by
        // FurnitureCover itself. The route there must still clear the player
        // (checked in OnPathReady); here we only reject spots that sit in
        // the player's space to begin with.
        // NOTE the vanishMinMove test: TryFindHidingSpot scores by distance
        // and returns the NEAREST cover — for a roach already tucked behind
        // the couch that is the spot it is standing on. Without this the
        // roach "relocated" a few centimetres and looked like it ignored the
        // player walking up to it.
        // Threat is the LIVE player, not vanishAnchor: on retries 2..8 the
        // anchor is however many seconds old, and a spot proven hidden from
        // where the player WAS is exactly how a roach ends up tucking into
        // plain view. (FarFromVanishAnchor below still uses the anchor on
        // purpose — that is the separate "keep away from where this vanish
        // started" rule.)
        if (preferCover
            && FurnitureCover.TryFindHidingSpot(
                transform.position, playerNow, actualFleeDistance * 1.2f, out spot)
            && HorizontalDistance(spot, transform.position) >= vanishMinMove
            && FarFromVanishAnchor(spot) && !IsNearClaimedSpot(spot)
            && !TooCloseToPlayer(spot, playerNow)
            && !MovesTowardPlayer(spot, playerNow))
        {
            return true;
        }

        // Candidate pool: random samples across the flee annulus, PLUS spots
        // tucked against the walls at every corner of the room outline.
        // Corners are seeded explicitly because uniform sampling almost never
        // lands in an alcove, and an alcove (or doorway recess, or L-corner)
        // is the only place a wall can hide anything — a flat wall has no far
        // side to stand on. In a plain rectangular room these simply fail the
        // occlusion test below and cost nothing but the sampling.
        vanishCandidates.Clear();
        for (int i = 0; i < 30; i++)
        {
            Vector3 dir = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f) * Vector3.forward;
            float dist = Random.Range(actualFleeDistance * 0.4f, actualFleeDistance * 1.2f);
            Vector3 sample = transform.position + dir * dist;
            sample.y = transform.position.y;
            vanishCandidates.Add(sample);
        }
        FurnitureCover.CollectCornerSpots(
            transform.position, actualFleeDistance * 1.2f, edgeMargin, vanishCandidates);

        // Every candidate must be valid, unclaimed and out of the player's
        // space — then it is bucketed by HOW it is unseen. The "don't come
        // near me" rule is enforced on the ROUTE by the PlayerBubbleCosts
        // traversal provider (see StartRequest) — a spot behind the player
        // reached by a clear path around the room is fine; a route that
        // would sweep past the player is simply never the cheapest one.
        var occluded = new List<Vector3>();
        var uncovered = new List<Vector3>();
        for (int i = 0; i < vanishCandidates.Count; i++)
        {
            Vector3 candidate = vanishCandidates[i];

            if (HorizontalDistance(candidate, transform.position) < vanishMinMove) continue;
            if (!FarFromVanishAnchor(candidate)) continue;
            if (!IsValidPoint(candidate)) continue;
            if (IsNearClaimedSpot(candidate)) continue;
            if (TooCloseToPlayer(candidate, playerNow)) continue;
            if (MovesTowardPlayer(candidate, playerNow)) continue;

            if (IsOccludedFromPlayer(candidate)) occluded.Add(candidate);
            else uncovered.Add(candidate); // no cover here — only an edge seed
        }

        // Nothing physically hides these ones — the player merely isn't
        // facing that way — so they only count if they already sit SNUG
        // against a wall or furniture edge, read straight off the Stage-1
        // cost field: node penalty 0 means "within the baker's comfort
        // distance of an edge", anything above is open floor, which is not
        // a destination at all (the v2.4 three-to-a-spray parking spot).
        // The corner spots from CollectCornerSpots are snug by
        // construction, so this tier is carried by them plus whatever
        // annulus samples happen to land against something. Edge spots at
        // the player's back stay banned (v2.7 rule). Replaced the v2.6
        // per-candidate TryFindEdgeSpot snapping: one O(1) graph lookup
        // instead of scanning every wall and furniture piece per candidate,
        // and no more herding several roaches onto the same "nearest" edge.
        var edgeHug = new List<Vector3>();
        if (edgeAffinity)
        {
            for (int i = 0; i < uncovered.Count; i++)
            {
                if (NodePenaltyAt(uncovered[i]) == 0 && !IsBehindPlayer(uncovered[i]))
                    edgeHug.Add(uncovered[i]);
            }
        }

        if (!preferCover)
        {
            // Chased roach: pure random over every tucked spot, so repeated
            // flushes scatter instead of converging on one piece of cover.
            occluded.AddRange(edgeHug);
            edgeHug.Clear();
        }

        // Real cover, else pressed against an edge somewhere the player is
        // not standing in front of. Open floor is no longer a destination at
        // all — "he isn't looking that way" stopped counting as hidden.
        var pool = occluded.Count > 0 ? occluded : edgeHug;
        if (pool.Count > 0)
        {
            spot = pool[Random.Range(0, pool.Count)];
            return true;
        }

        spot = default;
        return false;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        return new Vector2(a.x - b.x, a.z - b.z).magnitude;
    }

    private bool PlayerAvoidanceOn => playerAvoidRadius > 0.01f;

    // TrimAtPlayerZone / RouteApproachesPlayer used to live here — v2.2's
    // post-hoc route surgery, replaced by the PlayerBubbleCosts traversal
    // provider (player avoidance as path cost, applied in StartRequest).

    private static float RouteLength(List<Vector3> points)
    {
        float len = 0f;
        for (int i = 1; i < points.Count; i++)
            len += HorizontalDistance(points[i - 1], points[i]);
        return len;
    }

    /// <summary>Horizontal distance test against the player-avoidance radius.</summary>
    private bool TooCloseToPlayer(Vector3 pos, Vector3 playerPos)
    {
        return PlayerAvoidanceOn && HorizontalDistance(pos, playerPos) < playerAvoidRadius;
    }

    /// <summary>
    /// True when settling at this spot would bring the roach meaningfully
    /// CLOSER to the player than it already stands (0.2m tolerance so a
    /// sideways duck isn't rejected). The panic cover dash always had this
    /// gate; the vanish picker did not — so a calm roach 4m away could
    /// legally pick an occluded spot 1.7m from the player and run 2.3m
    /// TOWARD him to reach it. Hiding must never close the distance.
    /// </summary>
    private bool MovesTowardPlayer(Vector3 pos, Vector3 playerPos)
    {
        return HorizontalDistance(pos, playerPos)
             < HorizontalDistance(transform.position, playerPos) - 0.2f;
    }

    /// <summary>
    /// True when the computed route enters the player's avoidance radius
    /// anywhere (sampled every 0.25m; a start already inside the radius is
    /// exempt — leaving is unavoidable). Applied ONLY to VOLUNTARY
    /// relocation routes (cover dash, vanish): the cost bubble makes such
    /// routes rare, but a short skim through its outer rim is cheap enough
    /// that the pathfinder accepts it when the clean detour is long —
    /// device-observed 2026-07-20 as roaches "hiding somewhere their path
    /// passes me". A hide spot whose route passes the player is simply the
    /// wrong spot; the picker has plenty of other candidates. Panic flees
    /// and breakouts stay cost-based — for a cornered roach, near-player
    /// ground may genuinely be the only way out (Yossi's rule: necessary
    /// pass = allowed, voluntary approach = never).
    /// </summary>
    private bool RoutePassesPlayer(List<Vector3> points)
    {
        if (!PlayerAvoidanceOn) return false;
        Transform cam = GetCameraTransform();
        if (cam == null || points.Count == 0) return false;

        Vector2 player = new Vector2(cam.position.x, cam.position.z);
        float r = playerAvoidRadius;
        bool leftZone = (new Vector2(points[0].x, points[0].z) - player).magnitude >= r;

        for (int i = 1; i < points.Count; i++)
        {
            Vector3 a = points[i - 1], b = points[i];
            int steps = Mathf.Max(1, Mathf.CeilToInt(HorizontalDistance(a, b) / 0.25f));
            for (int s = 1; s <= steps; s++)
            {
                Vector3 pt = Vector3.Lerp(a, b, s / (float)steps);
                float d = (new Vector2(pt.x, pt.z) - player).magnitude;
                if (!leftZone) { if (d >= r) leftZone = true; continue; }
                if (d < r) return true;
            }
        }
        return false;
    }

    private bool FarFromVanishAnchor(Vector3 pos)
    {
        return new Vector2(pos.x - vanishAnchor.x, pos.z - vanishAnchor.z).magnitude
               >= playerScareDistance;
    }

    private bool IsNearClaimedSpot(Vector3 pos)
    {
        foreach (var kv in claimedSpots)
        {
            if (kv.Key == this) continue;
            if (new Vector2(pos.x - kv.Value.x, pos.z - kv.Value.z).magnitude < ClaimSeparation)
                return true;
        }
        return false;
    }

    private void ReleaseClaim() => claimedSpots.Remove(this);

    // ─── State: Hiding ──────────────────────────────────────────────────────────

    private void EnterHiding()
    {
        currentState = CockroachState.Hiding;
        alarmedRelocation = false;
        hideApproachRefTime = -1f; // fresh hiding place, fresh approach measurement
        motor.Stop();
    }

    private float hideCheckTimer;

    private void UpdateHiding()
    {
        hideCheckTimer -= Time.deltaTime;
        if (hideCheckTimer > 0f) return;
        hideCheckTimer = 0.15f;

        // The player closing in makes the roach move again — no light
        // needed. Real roaches feel the footsteps. Alarmed response:
        // a hide-to-hide relocation, not an open panic.
        if (IsPlayerTooClose())
        {
            HidePressure();
            return;
        }

        Transform cam = GetCameraTransform();
        if (cam == null) return;

        // ...but playerScareDistance alone is far too late to survive. The
        // roach runs at ~0.75 m/s and a relocation is ~2.5m, so it needs
        // roughly 3 seconds to reach new cover — while a walking player
        // crosses those 2m in under 2. It was always caught mid-run, and
        // alarmedRelocation (see UpdateVanishing) means it won't even
        // re-react on the way. So a hidden roach listens from further out,
        // and leaves while it still has the room to get somewhere.
        //
        // Gated on the player actually CLOSING IN: at 3.5m a plain radius
        // test would flush every hidden roach the moment the player stands
        // anywhere near the middle of a small room.
        Vector3 toPlayer = cam.position - transform.position;
        float horizontalDist = new Vector2(toPlayer.x, toPlayer.z).magnitude;

        if (horizontalDist < hideAlertDistance && IsPlayerApproaching(horizontalDist))
        {
            lastLightPosition = cam.position;
            lastLightHitTime = Time.time;
            HidePressure();
            return;
        }

        // Flushed out: the player is close AND has a clear line of sight
        float playerDist = Vector3.Distance(cam.position, transform.position);
        bool exposed = !FurnitureCover.IsSegmentBlocked(
            cam.position, transform.position + Vector3.up * 0.02f);

        if (exposed && playerDist < flushDistance)
        {
            lastLightPosition = cam.position;
            lastLightHitTime = Time.time;
            HidePressure();
        }
    }

    /// <summary>
    /// True when the player has measurably closed in since the last reference
    /// sample. See HideApproachWindow/HideApproachMargin for why this compares
    /// against a held sample instead of the previous tick.
    /// </summary>
    private bool IsPlayerApproaching(float currentDist)
    {
        if (hideApproachRefTime < 0f)
        {
            hideApproachRef = currentDist;
            hideApproachRefTime = Time.time;
            return false; // nothing to compare against yet
        }

        bool closing = currentDist < hideApproachRef - HideApproachMargin;

        // Roll the reference forward once it has fired, once the window has
        // elapsed, or the moment the player backs off — otherwise someone who
        // retreats and returns is measured against a stale, closer sample and
        // never registers as approaching again.
        if (closing || currentDist > hideApproachRef
            || Time.time - hideApproachRefTime > HideApproachWindow)
        {
            hideApproachRef = currentDist;
            hideApproachRefTime = Time.time;
        }

        return closing;
    }

    /// <summary>
    /// The player is pressing this hidden roach (beam, footsteps, or eye
    /// contact). Alarmed response (Yossi 2026-07-19): relocate hide-to-hide
    /// instead of bolting across open floor. The first shove goes to the
    /// nearest prism/wall cover; if the player FOLLOWS (another shove within
    /// hideChaseMemory), the roach picks a RANDOM hide spot anywhere — the
    /// route still never passes near the player unless the roach is trapped.
    /// Works exactly the same while the roach is occluded and not rendered.
    /// </summary>
    private void HidePressure()
    {
        if (Time.time - lastHidePressureTime > hideChaseMemory)
            hidePressureStrikes = 0;
        lastHidePressureTime = Time.time;
        hidePressureStrikes++;

        BeginVanish(preferCover: hidePressureStrikes < 2, alarmed: true);
    }

    // ─── State: Idle entry / Dead ───────────────────────────────────────────────

    private void EnterIdle()
    {
        currentState = CockroachState.Idle;
        alarmedRelocation = false;
        hidePressureStrikes = 0; // calm again — the chase is forgotten
        motor.Stop();
        ReleaseClaim();
        idleDartTimer = Random.Range(1f, 3f);

        if (scurryAudio != null && scurryAudio.isPlaying)
            scurryAudio.Stop();
    }

    private void EnterDead()
    {
        if (isDead) return;
        isDead = true;
        currentState = CockroachState.Dead;
        motor.Stop();
        ReleaseClaim();

        if (animator != null) animator.speed = 0f;

        // Death flip starts from a stable snapshot (nothing else writes the
        // rotation now that the motor is stopped)
        deathStartRotation = transform.rotation;
        targetDeathRotation = transform.rotation * Quaternion.Euler(180f, 0f, 0f);
        deathTimer = 0f;

        if (deathAudio != null && deathAudio.clip != null)
            deathAudio.Play();
        if (scurryAudio != null && scurryAudio.isPlaying)
            scurryAudio.Stop();

        var col = GetComponent<Collider>();
        if (col != null) col.enabled = false;

        // The swarm notices the kill (Yossi 2026-07-19) — neighbours scatter
        // instead of queueing up to be sprayed one by one
        AlertNeighbours();

        if (InfestGameManager.Instance != null)
            InfestGameManager.Instance.OnCockroachKilled();

        Debug.Log($"[Cockroach] Dead at {transform.position}");
    }

    /// <summary>Wakes every living roach within deathAlarmRadius of this corpse.</summary>
    private void AlertNeighbours()
    {
        if (deathAlarmRadius <= 0.01f) return;

        for (int i = 0; i < allRoaches.Count; i++)
        {
            var other = allRoaches[i];
            if (other == null || other == this || other.isDead) continue;
            if (HorizontalDistance(other.transform.position, transform.position) > deathAlarmRadius)
                continue;

            other.OnNeighbourDied();
        }
    }

    /// <summary>
    /// A roach died close by. The player did it, so the player is the threat:
    /// a resting roach reacts exactly as it would to being spotted — hidden
    /// ones relocate to fresh cover, idle ones bolt. Roaches already running
    /// just get their panic refreshed.
    /// </summary>
    private void OnNeighbourDied()
    {
        if (isDead) return;

        lastLightPosition = GetThreatPosition();
        lastLightHitTime = Time.time;

        if (currentState == CockroachState.Hiding) HidePressure();
        else if (currentState == CockroachState.Idle) EnterStartled();
        // Startled/Fleeing/Vanishing: the refreshed threat stamp keeps them
        // moving; an alarmed relocation still commits to its route
    }

    private void UpdateDead()
    {
        deathTimer += Time.deltaTime;
        float flip = Mathf.Clamp01(deathTimer * deathFlipSpeed);
        transform.rotation = Quaternion.Slerp(deathStartRotation, targetDeathRotation, flip);

        if (deathTimer >= deathDestroyDelay)
            Destroy(gameObject);
    }

    // ─── Path plumbing ──────────────────────────────────────────────────────────

    private static bool GraphAvailable()
    {
        return AstarPath.active != null
            && AstarPath.active.data.graphs != null
            && AstarPath.active.data.graphs.Length > 0;
    }

    /// <summary>
    /// Stage-1 thigmotaxis penalty of the walkable graph node under a point:
    /// 0 = snug against a wall/furniture edge (within the baker's comfort
    /// distance), higher = open floor. The graph doubles as a prebaked
    /// "how exposed is this spot" field, so the vanish picker never has to
    /// re-derive edge geometry from MRUK. Returns 0 with no graph
    /// (graph-less rooms use fallback movement anyway). NOTE: with
    /// openFloorPenalty = 0 on the baker every node reads 0 too, so
    /// disabling Stage 1 also softens the edge-hug tier back to accepting
    /// any not-behind-player spot (v2.6 behaviour).
    /// </summary>
    private uint NodePenaltyAt(Vector3 pos)
    {
        if (!GraphAvailable()) return 0;
        var nn = AstarPath.active.GetNearest(pos, NearestNodeConstraint.Walkable);
        return nn.node != null ? nn.node.Penalty : 0u;
    }

    /// <summary>
    /// Sends a path request through the Seeker with a request id, so a stale
    /// callback (cancelled or superseded request) can never drive the motor.
    /// </summary>
    private void StartRequest(Path p, RequestKind kind)
    {
        requestKind = kind;
        lastRequestTime = Time.time;

        // Player avoidance as path COST, not post-hoc route surgery: ground
        // near the player is expensive, so A* curves around them whenever a
        // detour exists — and routes near/under them only when that is the
        // sole affordable way out (the necessity dart, emerging naturally).
        // The player position is snapshotted HERE on the main thread; the
        // provider runs on the pathfinding threads and must never touch the
        // camera transform. (Seeker only fills the provider if it is null,
        // so this per-path assignment is never overwritten.)
        if (PlayerAvoidanceOn && playerAvoidCost > 0f)
        {
            Transform cam = GetCameraTransform();
            if (cam != null)
                p.traversalCosts.traversalProvider =
                    new PlayerBubbleCosts(cam.position, playerAvoidRadius, playerAvoidCost);
        }

        int id = ++requestId;
        seeker.StartPath(p, path => OnPathReady(path, id));
    }

    private void OnPathReady(Path p, int id)
    {
        if (id != requestId || isDead) return; // superseded meanwhile
        RequestKind kind = requestKind;
        requestKind = RequestKind.None;

        bool ok = !p.error && p.vectorPath != null && p.vectorPath.Count > 0;
        if (!ok)
        {
            OnPathFailed(kind);
            return;
        }

        // Copy the points — the path object may be pooled and reused
        var points = new List<Vector3>(p.vectorPath);

        switch (kind)
        {
            case RequestKind.Flee:
            case RequestKind.Breakout:
                if (currentState == CockroachState.Fleeing)
                {
                    // Player avoidance is baked into the path COST (see
                    // StartRequest): the route already curves around the
                    // player whenever a detour exists, and a genuinely
                    // trapped roach gets the near/under-player route
                    // naturally — no trimming, no trapped verdict.
                    motor.FollowPath(points);
                }
                break;

            case RequestKind.CoverDash:
                if (currentState != CockroachState.Fleeing) break;
                Vector3 dashEnd = points[points.Count - 1];
                bool dashReached = new Vector2(dashEnd.x - dashSpot.x, dashEnd.z - dashSpot.z).magnitude <= 0.3f;
                if (!dashReached || RoutePassesPlayer(points))
                {
                    // Cover unreachable, or ducking there would mean passing
                    // the player — take a plain panic run instead
                    RequestFleeRun();
                }
                else
                {
                    motor.FollowPath(points);
                }
                break;

            case RequestKind.Vanish:
                if (currentState != CockroachState.Vanishing) break;
                // Unreachable spot, OR a hide route that would pass the
                // player — reject and try the next hiding option
                Vector3 end = points[points.Count - 1];
                bool reached = new Vector2(end.x - vanishSpot.x, end.z - vanishSpot.z).magnitude <= 0.3f;
                if (!reached || RoutePassesPlayer(points))
                {
                    ReleaseClaim();
                    TryNextVanishOption();
                }
                else
                {
                    motor.FollowPath(points);
                }
                break;

            case RequestKind.Dart:
                if (currentState == CockroachState.Idle)
                    motor.FollowPath(points);
                break;
        }
    }

    private void OnPathFailed(RequestKind kind)
    {
        switch (kind)
        {
            case RequestKind.Flee:
            case RequestKind.Breakout:
                if (currentState == CockroachState.Fleeing && directMovementFallback)
                    FallbackStraightRun();
                break;
            case RequestKind.CoverDash:
                if (currentState == CockroachState.Fleeing)
                    RequestFleeRun();
                break;
            case RequestKind.Vanish:
                if (currentState == CockroachState.Vanishing)
                {
                    ReleaseClaim();
                    TryNextVanishOption();
                }
                break;
            // Dart: just wait for the next dart timer
        }
    }

    /// <summary>
    /// Straight-line flee for graph-less rooms: deviate the committed
    /// heading (small angles first), shorten as a last resort, and hand the
    /// motor a plain two-point route. The motor's per-step veto keeps it
    /// inside the room, and its Blocked event re-plans on a dead end.
    /// </summary>
    private void FallbackStraightRun()
    {
        float[] deviations = { 0f, 45f, -45f, 90f, -90f, 135f, -135f, 180f };
        float[] lengths = { 1f, 0.6f, 0.35f };

        foreach (float len in lengths)
        {
            foreach (float dev in deviations)
            {
                Vector3 dir = Quaternion.Euler(0f, dev, 0f) * fleeDir;
                Vector3 end = transform.position + dir * (actualFleeDistance * len);
                end.y = transform.position.y;
                Vector3 mid = transform.position + dir * (actualFleeDistance * len * 0.5f);
                mid.y = end.y;
                if (IsValidPoint(end) && IsValidPoint(mid))
                {
                    fleeDir = dir;
                    motor.FollowPath(new List<Vector3> { transform.position, end });
                    return;
                }
            }
        }

        // Fully boxed in — the dart under the player is the only move left
        if (TryDartUnderPlayer(out List<Vector3> dart))
        {
            Debug.Log($"[Cockroach:{GetInstanceID()}] DART taken (trigger=boxed-fallback, playerDist={DebugPlayerDistance():F2}m)");
            motor.FollowPath(dart);
        }
    }

    // ─── Motor events ───────────────────────────────────────────────────────────

    private void OnMotorArrived()
    {
        switch (currentState)
        {
            case CockroachState.Fleeing:
                // Cover dash completed → tucked in, done
                if (dashToCover)
                {
                    dashToCover = false;
                    EnterHiding();
                    break;
                }
                // Still under pressure → look for cover from where we now
                // stand, and only take another open run if there is none
                if (IsLitRecently() || IsPlayerWithinScareDistance())
                {
                    fleeDir = AwayFromThreatDir();
                    if (!TryStartCoverDash()) RequestFleeRun();
                }
                else
                {
                    BeginVanish();
                }
                break;

            case CockroachState.Vanishing:
                EnterHiding();
                break;

            // Idle dart: rest until the next dart timer
        }
    }

    private void OnMotorBlocked()
    {
        switch (currentState)
        {
            case CockroachState.Fleeing:
                // A blocked cover dash falls back to a plain run;
                // a blocked run breaks out sideways
                if (dashToCover)
                    RequestFleeRun();
                else
                    RequestBreakout();
                break;
            case CockroachState.Vanishing:
                ReleaseClaim();
                TryNextVanishOption();
                break;
            case CockroachState.Idle:
                break; // dart hit a wall — just rest
        }
    }

    // ─── Threat helpers ─────────────────────────────────────────────────────────

    private bool IsLitRecently() => Time.time - lastLightHitTime < 0.2f;

    /// <summary>
    /// True when the player stands within playerScareDistance (HORIZONTAL
    /// distance — the head is ~1.5m above the floor, so 3D distance to a
    /// floor roach would never trigger). Also stamps the player as the
    /// current threat.
    /// </summary>
    private bool IsPlayerTooClose()
    {
        if (!IsPlayerWithinScareDistance()) return false;
        lastLightPosition = GetCameraTransform().position;
        lastLightHitTime = Time.time;
        return true;
    }

    private bool IsPlayerWithinScareDistance()
    {
        Transform cam = GetCameraTransform();
        if (cam == null) return false;
        Vector3 to = cam.position - transform.position;
        return new Vector2(to.x, to.z).magnitude < playerScareDistance;
    }

    /// <summary>Horizontal player distance, for the dart diagnostic logs.</summary>
    private float DebugPlayerDistance()
    {
        Transform cam = GetCameraTransform();
        return cam == null ? -1f : HorizontalDistance(transform.position, cam.position);
    }

    /// <summary>
    /// Escape heading: away from the threat, rotated by a random fan angle.
    /// ±60° keeps every result genuinely away-ish (never back toward the
    /// player). Fully random when the threat sits exactly on the roach.
    /// </summary>
    private Vector3 AwayFromThreatDir(float fanDegrees = 60f)
    {
        Vector3 away = transform.position - GetThreatPosition();
        away.y = 0f;
        if (away.sqrMagnitude < 0.0001f)
            return Quaternion.Euler(0f, Random.Range(0f, 360f), 0f) * Vector3.forward;
        return Quaternion.Euler(0f, Random.Range(-fanDegrees, fanDegrees), 0f) * away.normalized;
    }

    private Vector3 GetThreatPosition()
    {
        Transform cam = GetCameraTransform();
        if (cam != null) return cam.position;
        if (lastLightPosition != Vector3.zero) return lastLightPosition;
        return transform.position + new Vector3(Random.Range(-1f, 1f), 0f, Random.Range(-1f, 1f));
    }

    private static Transform GetCameraTransform()
    {
        if (cachedCameraTransform != null) return cachedCameraTransform;
        if (Camera.main != null)
        {
            cachedCameraTransform = Camera.main.transform;
        }
        else
        {
            var centerEye = GameObject.Find("CenterEyeAnchor");
            if (centerEye != null) cachedCameraTransform = centerEye.transform;
        }
        return cachedCameraTransform;
    }

    /// <summary>
    /// True when furniture or a wall physically blocks the player's line of
    /// sight to this position.
    ///
    /// This is now the ONLY definition of "hidden" the AI accepts for a
    /// destination (Yossi 2026-07-19). It previously also counted any point
    /// outside the player's forward view cone, which quietly made the single
    /// best-scoring hiding place in the room the open floor directly behind
    /// the player's back — so roaches looped around and tucked in behind him.
    /// Being unobserved is not the same as being covered, and only the latter
    /// survives the player turning his head.
    /// </summary>
    private bool IsOccludedFromPlayer(Vector3 pos)
    {
        Transform cam = GetCameraTransform();
        if (cam == null) return true;

        return FurnitureCover.IsSegmentBlocked(cam.position, pos + Vector3.up * 0.02f);
    }

    /// <summary>
    /// True when the position sits in the hemisphere behind the player.
    /// Applied to UNCOVERED fallback spots only: a roach that can't find real
    /// cover may still tuck against an edge, but never at the player's back.
    /// (A covered spot that happens to lie behind the player is fine — it is
    /// hidden by the furniture or wall, not by the player.)
    /// </summary>
    private bool IsBehindPlayer(Vector3 pos)
    {
        Transform cam = GetCameraTransform();
        if (cam == null) return false;

        Vector3 to = pos - cam.position;
        to.y = 0f;
        Vector3 fwd = cam.forward;
        fwd.y = 0f;
        if (to.sqrMagnitude < 0.0001f || fwd.sqrMagnitude < 0.0001f) return false;

        return Vector3.Angle(fwd, to) > 90f;
    }

    // ─── Geometry helpers (motor uses IsValidPoint for its per-step veto) ───────

    private static bool IsInsideRoom(Vector3 pos)
    {
        var room = MRUK.Instance != null ? MRUK.Instance.GetCurrentRoom() : null;
        if (room == null) return true;
        return room.IsPositionInRoom(pos, testVerticalBounds: false);
    }

    /// <summary>
    /// A point a roach may occupy: inside the scanned room and not inside a
    /// furniture volume prism (desks must be walked around, not through).
    /// </summary>
    public static bool IsValidPoint(Vector3 pos)
    {
        return IsInsideRoom(pos)
            && !FurnitureCover.IsInsideFurniture(pos + Vector3.up * 0.02f);
    }

    // ─── Visibility (fake occlusion by real furniture) ──────────────────────────

    private void UpdateVisibility()
    {
        visCheckTimer -= Time.deltaTime;
        if (visCheckTimer > 0f) return;
        visCheckTimer = 0.1f;

        Transform cam = GetCameraTransform();
        if (cam == null) return;

        bool visible = !FurnitureCover.IsSegmentBlocked(
            cam.position, transform.position + Vector3.up * 0.02f);
        if (visible == renderersVisible) return;

        renderersVisible = visible;
        foreach (var r in renderers)
            if (r != null) r.enabled = visible;

        // The instant a spooked roach drops out of the player's sight, use
        // the free moment to gain ground: go vanish relative to where the
        // player NOW stands. Edge-triggered; idle roaches are excluded.
        if (!visible && !isDead &&
            (currentState == CockroachState.Fleeing ||
             currentState == CockroachState.Vanishing ||
             currentState == CockroachState.Hiding))
        {
            lastLightPosition = cam.position;
            lastLightHitTime = Time.time - fleeLingerTime; // pressure counts as off
            motor.maxSpeed = actualFleeSpeed;
            // A mid-run alarmed relocation keeps its character (commitment +
            // spot tier) — just re-picked from the player's new viewpoint
            if (currentState == CockroachState.Vanishing)
                BeginVanish(vanishPreferCover, alarmedRelocation);
            else
                BeginVanish();
        }
    }

    // ─── Hiss ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Each roach is counted once when the player first comes within
    /// hissRange. Every roachesPerHiss-th such roach plays a single hiss —
    /// one hiss per ~25 roaches passed, never a chorus. The very first
    /// close encounter always hisses.
    /// </summary>
    private void TryHiss()
    {
        if (hissClip == null || scurryAudio == null) return;

        var cam = GetCameraTransform();
        if (cam == null) return;

        // HORIZONTAL distance — see IsPlayerTooClose
        Vector3 toCam = cam.position - transform.position;
        float dist = new Vector2(toCam.x, toCam.z).magnitude;
        if (dist > hissRange) return;

        if (!countedNearPlayer)
        {
            countedNearPlayer = true;
            roachesPassedNearPlayer++;
        }

        if (hasHissed) return;
        if (firstHissDone && roachesPassedNearPlayer < Mathf.Max(1, roachesPerHiss))
            return;

        firstHissDone = true;
        roachesPassedNearPlayer = 0;
        hasHissed = true;

        // The source is 3D-spatialized — the engine already attenuates by
        // distance, manual scaling made edge-of-range hisses inaudible
        scurryAudio.pitch = Random.Range(0.85f, 1.2f);
        scurryAudio.PlayOneShot(hissClip, 1f);
    }

    // ─── Animation ──────────────────────────────────────────────────────────────

    private void UpdateAnimator()
    {
        if (animator == null || isDead) return;

        // "Discovered" is the walking bool in Cockroach.controller; alert
        // (startled) and running states show the walk, resting states don't
        bool active = currentState == CockroachState.Startled
                   || currentState == CockroachState.Fleeing
                   || currentState == CockroachState.Vanishing
                   || (currentState == CockroachState.Idle && motor.IsMoving);
        animator.SetBool("Discovered", active);

        bool running = currentState == CockroachState.Fleeing
                    || currentState == CockroachState.Vanishing;
        animator.speed = running ? fleeAnimSpeed : idleAnimSpeed;
    }

    // ─── Public API (FlashlightController / SprayParticleKillRelay) ─────────────

    /// <summary>
    /// Called each frame by FlashlightController while the beam hits this
    /// cockroach. If the beam cone reaches the roach, it reacts —
    /// reliability beats realism for the core mechanic.
    /// </summary>
    public void OnLightHit(Vector3 lightPosition)
    {
        if (isDead) return;

        lastLightHitTime = Time.time;
        lastLightPosition = lightPosition;

        if (currentState == CockroachState.Hiding)
        {
            // Beamed in its hiding place: alarmed relocation to the next
            // cover, not an open panic (Yossi 2026-07-19)
            HidePressure();
        }
        else if (currentState == CockroachState.Idle
            || (currentState == CockroachState.Vanishing && !alarmedRelocation))
        {
            EnterStartled();
        }
        // Startled/Fleeing: the refreshed timestamps keep the panic alive.
        // Alarmed relocations commit — pressure re-checks on arrival.
    }

    /// <summary>Called by SprayParticleKillRelay when spray particles hit.</summary>
    public void TakeDamage(float amount)
    {
        if (isDead) return;

        currentHP -= amount;
        Debug.Log($"[Cockroach] Took {amount} damage, HP: {currentHP}/{maxHP}");

        if (currentHP <= 0f)
            EnterDead();
    }

    void OnDestroy()
    {
        claimedSpots.Remove(this);
        allRoaches.Remove(this);

        if (!isDead && InfestGameManager.Instance != null)
            InfestGameManager.Instance.OnCockroachRemoved();
    }
}

/// <summary>
/// The cockroach's legs: follows a route (a plain list of points) with
/// insect-style steering. Doesn't know or care where routes come from —
/// A*-grown paths and hand-built straight lines go through the same pipe.
///
///   - Near-instant acceleration, instant stops (insects have no inertia
///     at this scale — AIPath's smooth ramps read as "RC car").
///   - Pivot-in-place before sharp turns: roaches turn first, then run.
///   - Per-roach Perlin wobble bends runs into organic waves.
///   - Owns the transform completely; y is locked to the floor.
///   - Each step is vetoed against room bounds / furniture prisms
///     (CockroachBehavior.IsValidPoint); a vetoed stretch raises Blocked
///     so the brain can re-plan.
/// </summary>
public class CockroachMotor : MonoBehaviour
{
    [Tooltip("Current top speed — set by the brain per state")]
    public float maxSpeed = 0.5f;

    [Tooltip("Turn rate in degrees per second")]
    public float rotationSpeed = 900f;

    [Tooltip("Seconds to reach top speed (near-instant for insects)")]
    public float accelTime = 0.06f;

    [Tooltip("Sideways Perlin wobble amplitude (m) — organic waviness")]
    public float wobbleAmplitude = 0.03f;

    [Tooltip("Wobble frequency (Hz-ish)")]
    public float wobbleFrequency = 6f;

    [Tooltip("A waypoint is passed within this distance")]
    public float waypointRadius = 0.07f;

    [Tooltip("The route ends within this distance of its last point")]
    public float arriveRadius = 0.06f;

    [Tooltip("Angle above which the roach pivots in place before running")]
    public float pivotAngle = 70f;

    /// <summary>Raised when the end of the current route is reached.</summary>
    public event System.Action Arrived;

    /// <summary>Raised when forward progress was vetoed for a while (dead end).</summary>
    public event System.Action Blocked;

    public bool IsMoving => route != null;

    /// <summary>Current speed in m/s — usable for animation scaling.</summary>
    public float CurrentSpeed => speed;

    private List<Vector3> route;
    private int waypointIndex;
    private float speed;
    private float floorY;
    private float wobbleSeed;
    private float blockedTimer;

    void Awake()
    {
        wobbleSeed = Random.value * 100f;
        floorY = transform.position.y;
    }

    public void SetFloorY(float y) => floorY = y;

    /// <summary>Starts following a route. Replaces any current route.</summary>
    public void FollowPath(List<Vector3> points)
    {
        if (points == null || points.Count == 0)
        {
            Stop();
            return;
        }
        route = points;
        waypointIndex = 0;
        blockedTimer = 0f;
    }

    public void Stop()
    {
        route = null;
        speed = 0f;
    }

    void Update()
    {
        if (route == null) return;

        Vector3 pos = transform.position;

        // Advance past waypoints we're close to
        while (waypointIndex < route.Count - 1
               && Flat(route[waypointIndex] - pos).magnitude < waypointRadius)
            waypointIndex++;

        Vector3 to = Flat(route[waypointIndex] - pos);

        // Arrived?
        if (waypointIndex >= route.Count - 1 && to.magnitude < arriveRadius)
        {
            route = null;
            speed = 0f;
            Arrived?.Invoke();
            return;
        }

        // Steering direction with sideways Perlin wobble
        Vector3 dir = to.normalized;
        Vector3 side = Vector3.Cross(Vector3.up, dir);
        float wob = (Mathf.PerlinNoise(wobbleSeed, Time.time * wobbleFrequency) - 0.5f) * 2f;
        Vector3 steer = (dir + side * (wob * wobbleAmplitude * 10f)).normalized;

        // Turn toward the steering direction; sharp angles pivot in place
        Quaternion want = Quaternion.LookRotation(steer, Vector3.up);
        float angle = Quaternion.Angle(transform.rotation, want);
        transform.rotation = Quaternion.RotateTowards(
            transform.rotation, want, rotationSpeed * Time.deltaTime);

        float targetSpeed = angle > pivotAngle ? 0f : maxSpeed;
        if (targetSpeed > speed)
            speed = Mathf.MoveTowards(speed, targetSpeed,
                (maxSpeed / Mathf.Max(accelTime, 0.01f)) * Time.deltaTime);
        else
            speed = targetSpeed; // instant stop

        if (speed <= 0f)
        {
            blockedTimer = 0f;
            return; // pivoting
        }

        // Step along facing, locked to the floor; veto invalid ground.
        // Graph routes hug furniture legally, but the wobble could poke a
        // corner into a prism — retry the straight step before vetoing.
        Vector3 step = transform.forward * (speed * Time.deltaTime);
        Vector3 newPos = pos + step;
        newPos.y = floorY;

        if (!CockroachBehavior.IsValidPoint(newPos))
        {
            Vector3 straight = pos + dir * (speed * Time.deltaTime);
            straight.y = floorY;
            if (CockroachBehavior.IsValidPoint(straight))
            {
                newPos = straight;
            }
            else
            {
                // Both vetoed — accumulate toward a Blocked re-plan
                blockedTimer += Time.deltaTime;
                if (blockedTimer > 0.4f)
                {
                    Stop();
                    Blocked?.Invoke();
                }
                return;
            }
        }

        blockedTimer = 0f;
        transform.position = newPos;
    }

    private static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v;
    }
}

/// <summary>
/// A* traversal-cost provider that makes the ground around the player
/// EXPENSIVE rather than forbidden. The pathfinder then curves routes
/// around the player whenever any detour exists, and routes near/under
/// them only when that is the sole affordable way out — which is exactly
/// the necessity-dart rule, emerging from cost instead of being hand-coded
/// (replaced v2.2's TrimAtPlayerZone/RouteApproachesPlayer surgery).
/// The multiplier ramps LINEARLY from 1x at the bubble rim to
/// (1 + maxBoost)x at the player's feet, ON TOP of the default costs, so
/// the open-floor penalties baked by RuntimeNavMeshBaker keep applying.
/// Linear, not quadratic, ON PURPOSE: the original soft-rim quadratic
/// made ground 1.0-1.4m from the player cost only 1.05-2.3x, undercutting
/// the 3x open-floor penalty — the cheapest escape was to sneak along a
/// wall right past the player. Inside the radius this bubble must beat
/// the floor penalty by a clear margin at every distance.
/// IMMUTABLE, player position snapshotted at construction: path
/// calculation runs on the pathfinding threads and this class must never
/// read Unity state (camera, transforms) after construction.
/// </summary>
public class PlayerBubbleCosts : ITraversalProvider
{
    private readonly float px, pz;   // player floor position at request time
    private readonly float radius;   // bubble radius in meters
    private readonly float maxBoost; // extra cost multiplier at the center

    public PlayerBubbleCosts(Vector3 playerPos, float radius, float maxBoost)
    {
        px = playerPos.x;
        pz = playerPos.z;
        this.radius = radius;
        this.maxBoost = maxBoost;
    }

    public float GetTraversalCostMultiplier(ref TraversalCosts costs, GraphNode node)
    {
        Vector3 p = (Vector3)node.position;
        float dx = p.x - px, dz = p.z - pz;
        float t = 1f - Mathf.Clamp01(Mathf.Sqrt(dx * dx + dz * dz) / radius);
        return (1f + maxBoost * t)
             * DefaultITraversalProvider.GetTraversalCostMultiplier(ref costs, node);
    }
}
