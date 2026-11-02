using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Meta.XR.MRUtilityKit;
using Pathfinding;

/// <summary>
/// Spawns cockroaches using MRUK scene understanding data.
/// Spawns at wall-floor intersections, near/behind furniture, and in corners.
/// Uses continuous spawning: maintains maxAliveAtOnce cockroaches until
/// the total budget (from InfestGameManager) is reached.
/// Spawns out of the player's field of view (Left 4 Dead style).
/// </summary>
public class CockroachSpawner : MonoBehaviour
{
    [Header("Prefab")]
    [Tooltip("The cockroach prefab to instantiate")]
    public GameObject cockroachPrefab;

    [Header("Spawn Settings")]
    [Tooltip("Minimum distance between spawned cockroaches")]
    public float minDistanceBetween = 0.3f;

    [Tooltip("Enforced spawn spacing so roaches don't cluster into easy " +
             "multi-kills — the larger of this and Min Distance Between wins")]
    public float minSpawnSeparation = 0.6f;

    [Tooltip("Time between spawn attempts (seconds)")]
    public float spawnInterval = 0.7f;

    [Tooltip("How many cockroaches to spawn in the initial batch")]
    public int initialBatchSize = 8;

    [Tooltip("Player's field of view angle — cockroaches spawn outside this cone")]
    public float playerFOVAngle = 90f;

    [Tooltip("Minimum distance from player to spawn")]
    public float minDistanceFromPlayer = 1.5f;

    [Tooltip("Offset above floor to spawn cockroaches")]
    public float floorOffset = 0.005f;

    [Tooltip("Max horizontal distance a candidate point may be moved to reach walkable floor — points further from the floor grid are discarded")]
    public float maxSnapDistance = 0.35f;

    [Tooltip("Minimum distance a spawn point must keep from the room walls. " +
             "Near-wall spots (e.g. behind furniture) are fine, ON the wall is not.")]
    public float minDistanceFromWalls = 0.12f;

    // ─── Internal ───────────────────────────────────────────────────────────────
    private List<Vector3> spawnPoints = new List<Vector3>();
    private List<Vector3> roomOutline;
    private float floorY = 0f;
    private List<GameObject> aliveCockroaches = new List<GameObject>();
    private Transform playerCamera;
    private bool isSpawning = false;
    private float spawnTimer = 0f;
    private MRUKRoom currentRoom;

    // ─── Lifecycle ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Called after NavMesh is baked and game is ready to start spawning.
    /// Typically connected to RuntimeNavMeshBaker.OnNavMeshReady via events,
    /// or called directly.
    /// </summary>
    public void BeginSpawning()
    {
        Debug.Log("[CockroachSpawner] BeginSpawning called");

        // Find the player camera
        playerCamera = Camera.main?.transform;
        if (playerCamera == null)
        {
            var centerEye = GameObject.Find("CenterEyeAnchor");
            if (centerEye != null)
                playerCamera = centerEye.transform;
        }

        // Gather spawn points from MRUK
        if (MRUK.Instance != null)
        {
            currentRoom = MRUK.Instance.GetCurrentRoom();
        }

        if (currentRoom != null)
        {
            GatherSpawnPoints(currentRoom);
        }
        else
        {
            Debug.LogWarning("[CockroachSpawner] No MRUK room found, using fallback spawn positions");
            GenerateFallbackSpawnPoints();
        }

        Debug.Log($"[CockroachSpawner] Found {spawnPoints.Count} spawn points");

        // Initial batch spawn
        StartCoroutine(SpawnInitialBatch());

        isSpawning = true;
    }

    /// <summary>
    /// Starts a fresh round: clears leftover roaches and spawns a new initial
    /// batch. Reuses the spawn points gathered on the first BeginSpawning call.
    /// </summary>
    public void RestartSpawning()
    {
        StopAllCoroutines();

        foreach (var roach in aliveCockroaches)
        {
            if (roach != null) Destroy(roach);
        }
        aliveCockroaches.Clear();
        spawnTimer = spawnInterval;

        // Spawn points were never gathered (first round failed?) — do full init
        if (spawnPoints.Count == 0)
        {
            BeginSpawning();
            return;
        }

        StartCoroutine(SpawnInitialBatch());
        isSpawning = true;
    }

    void Update()
    {
        if (!isSpawning) return;
        if (InfestGameManager.Instance == null) return;
        if (!InfestGameManager.Instance.IsGameActive) return;

        // Clean up null references (destroyed cockroaches)
        aliveCockroaches.RemoveAll(c => c == null);

        // Continuous spawning
        spawnTimer -= Time.deltaTime;
        if (spawnTimer <= 0f)
        {
            spawnTimer = spawnInterval;
            TrySpawnCockroach();
        }
    }

    // ─── Spawn Point Gathering ──────────────────────────────────────────────────

    private void GatherSpawnPoints(MRUKRoom room)
    {
        spawnPoints.Clear();
        floorY = 0f;

        // Get floor Y position. Only the first floor anchor is used — the A* grid
        // RuntimeNavMeshBaker bakes is a single flat plane at one Y, and every
        // spawn point below is flattened onto that same floor.
        var floorAnchor = room.FloorAnchors.Count > 0 ? room.FloorAnchors[0] : null;
        if (floorAnchor != null)
        {
            floorY = floorAnchor.GetAnchorCenter().y;
        }

        // Cache the outline for wall-distance validation
        roomOutline = room.GetRoomOutline();

        List<Vector3> candidates = new List<Vector3>();

        // Near/behind furniture anchors ONLY — wall bases and room corners
        // are deliberately not used: roaches must not spawn on the walls.
        var anchors = room.Anchors;
        if (anchors != null)
        {
            foreach (var anchor in anchors)
            {
                // Skip floor and ceiling
                if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.FLOOR) ||
                    anchor.HasAnyLabel(MRUKAnchor.SceneLabels.CEILING) ||
                    anchor.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE))
                    continue;

                // This is furniture — spawn at its base edges
                Vector3 anchorPos = anchor.GetAnchorCenter();
                anchorPos.y = floorY;

                // MRUK anchor axes are often tilted or Z-up, so flatten them to
                // the floor plane — otherwise the offsets below push points
                // into the air or under the floor.
                Vector3 forward = Vector3.ProjectOnPlane(anchor.transform.forward, Vector3.up).normalized;
                Vector3 right = Vector3.ProjectOnPlane(anchor.transform.right, Vector3.up).normalized;
                if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
                if (right.sqrMagnitude < 0.01f) right = Vector3.Cross(Vector3.up, forward);

                // Use the anchor's volume bounds if available
                var volumeBounds = anchor.VolumeBounds;
                float halfWidth = volumeBounds.HasValue ? volumeBounds.Value.size.x / 2f : 0.3f;
                float halfDepth = volumeBounds.HasValue ? volumeBounds.Value.size.z / 2f : 0.3f;

                // Spawn points around furniture base
                candidates.Add(anchorPos + forward * (halfDepth + 0.05f));
                candidates.Add(anchorPos - forward * (halfDepth + 0.05f));
                candidates.Add(anchorPos + right * (halfWidth + 0.05f));
                candidates.Add(anchorPos - right * (halfWidth + 0.05f));

                // Corner positions
                candidates.Add(anchorPos + forward * (halfDepth + 0.05f) + right * (halfWidth + 0.05f));
                candidates.Add(anchorPos + forward * (halfDepth + 0.05f) - right * (halfWidth + 0.05f));
                candidates.Add(anchorPos - forward * (halfDepth + 0.05f) + right * (halfWidth + 0.05f));
                candidates.Add(anchorPos - forward * (halfDepth + 0.05f) - right * (halfWidth + 0.05f));
            }
        }

        // Fallback: room has no usable furniture — use open-floor positions
        // kept well away from the walls
        if (candidates.Count == 0)
        {
            Debug.LogWarning("[CockroachSpawner] No furniture anchors found — using open-floor spawn points");
            for (int i = 0; i < 20; i++)
            {
                if (room.GenerateRandomPositionOnSurface(
                    MRUK.SurfaceType.FACING_UP,
                    0.5f, // minDistanceToEdge — stay clearly off the walls
                    new LabelFilter(MRUKAnchor.SceneLabels.FLOOR),
                    out Vector3 randPos,
                    out Vector3 randNormal))
                {
                    randPos.y = floorY;
                    candidates.Add(randPos);
                }
            }
        }

        // Nudge all points slightly inward from walls to avoid spawning inside walls
        NudgePointsInward(room, candidates);

        // Keep only points that are actually on walkable floor — discards
        // points in the air, below the floor, outside the room, or inside
        // furniture volumes.
        foreach (var candidate in candidates)
        {
            if (TrySnapToFloor(candidate, out Vector3 onFloor))
                spawnPoints.Add(onFloor);
        }

        Debug.Log($"[CockroachSpawner] {spawnPoints.Count}/{candidates.Count} candidate points passed floor validation");

        // Safety net: if validation rejected everything (e.g. no walkable
        // graph), fall back to the raw candidates flattened onto the floor —
        // still refusing points that sit on a wall.
        if (spawnPoints.Count == 0)
        {
            Debug.LogWarning("[CockroachSpawner] No candidates passed floor validation — using flattened candidates");
            foreach (var candidate in candidates)
            {
                Vector3 flat = new Vector3(candidate.x, floorY + floorOffset, candidate.z);
                if (DistanceToRoomEdge(flat) >= minDistanceFromWalls &&
                    !FurnitureCover.IsInsideFurniture(flat + Vector3.up * 0.02f))
                    spawnPoints.Add(flat);
            }

            // Absolute last resort — better a wall-adjacent roach than none
            if (spawnPoints.Count == 0)
                foreach (var candidate in candidates)
                    spawnPoints.Add(new Vector3(candidate.x, floorY + floorOffset, candidate.z));
        }
    }

    private void NudgePointsInward(MRUKRoom room, List<Vector3> points)
    {
        var outline = room.GetRoomOutline();
        if (outline == null || outline.Count < 3) return;

        // Calculate room center
        Vector3 center = Vector3.zero;
        foreach (var pt in outline) center += pt;
        center /= outline.Count;

        // Nudge each point slightly toward center on the horizontal plane only
        for (int i = 0; i < points.Count; i++)
        {
            Vector3 pt = points[i];
            Vector3 toCenter = center - pt;
            toCenter.y = 0f;
            toCenter.Normalize();
            points[i] = pt + toCenter * 0.1f;
        }
    }

    /// <summary>
    /// Projects a point onto the floor plane and verifies it lies inside the
    /// room on walkable ground (via the baked A* grid). Returns false for
    /// points in the air, below the floor, outside the room, or blocked by
    /// furniture.
    /// </summary>
    private bool TrySnapToFloor(Vector3 pos, out Vector3 result)
    {
        result = new Vector3(pos.x, floorY + floorOffset, pos.z);

        // Must be inside the room footprint
        if (currentRoom != null && !currentRoom.IsPositionInRoom(result, testVerticalBounds: false))
            return false;

        // Must keep a minimum clearance from the walls
        if (DistanceToRoomEdge(result) < minDistanceFromWalls)
            return false;

        // Never inside a furniture volume prism (e.g. under/inside a desk)
        if (FurnitureCover.IsInsideFurniture(result + Vector3.up * 0.02f))
            return false;

        // Must land on a walkable node of the floor grid
        if (AstarPath.active != null && AstarPath.active.data.graphs != null && AstarPath.active.data.graphs.Length > 0)
        {
            var nearest = AstarPath.active.GetNearest(result, NearestNodeConstraint.Walkable);
            if (nearest.node == null || !nearest.node.Walkable)
                return false;

            Vector3 nodePos = (Vector3)nearest.node.position;
            float horizontalDist = Vector2.Distance(
                new Vector2(nodePos.x, nodePos.z),
                new Vector2(result.x, result.z));
            if (horizontalDist > maxSnapDistance)
                return false;

            result = new Vector3(nodePos.x, floorY + floorOffset, nodePos.z);

            // The snap may have moved the point — re-check the constraints
            if (DistanceToRoomEdge(result) < minDistanceFromWalls)
                return false;
            if (FurnitureCover.IsInsideFurniture(result + Vector3.up * 0.02f))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Horizontal distance from a point to the nearest room-outline segment
    /// (i.e. to the nearest wall base). MaxValue when no outline is known.
    /// </summary>
    private float DistanceToRoomEdge(Vector3 p)
    {
        if (roomOutline == null || roomOutline.Count < 2) return float.MaxValue;

        Vector2 pt = new Vector2(p.x, p.z);
        float best = float.MaxValue;
        for (int i = 0; i < roomOutline.Count; i++)
        {
            Vector2 a = new Vector2(roomOutline[i].x, roomOutline[i].z);
            Vector2 b = new Vector2(roomOutline[(i + 1) % roomOutline.Count].x,
                                    roomOutline[(i + 1) % roomOutline.Count].z);
            Vector2 ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(pt - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
            best = Mathf.Min(best, Vector2.Distance(pt, a + ab * t));
        }
        return best;
    }

    private void GenerateFallbackSpawnPoints()
    {
        floorY = 0f;

        // Generate points in a circle around origin, snapped onto the
        // walkable floor grid when one exists
        for (int i = 0; i < 20; i++)
        {
            float angle = i * Mathf.PI * 2f / 20f;
            float radius = Random.Range(1.5f, 3f);
            Vector3 pos = new Vector3(
                Mathf.Cos(angle) * radius,
                floorOffset,
                Mathf.Sin(angle) * radius
            );

            if (TrySnapToFloor(pos, out Vector3 onFloor))
                spawnPoints.Add(onFloor);
            else
                spawnPoints.Add(pos);
        }
    }

    // ─── Spawning Logic ─────────────────────────────────────────────────────────

    private IEnumerator SpawnInitialBatch()
    {
        // Wait a frame for everything to initialize
        yield return null;

        for (int i = 0; i < initialBatchSize; i++)
        {
            // A failed attempt (every free spot in view / too close) is normal
            // with the hard spawn rules — keep going, the continuous spawner
            // fills any shortfall once the player looks elsewhere
            TrySpawnCockroach();
            yield return new WaitForSeconds(0.1f);
        }
    }

    private bool TrySpawnCockroach()
    {
        if (InfestGameManager.Instance == null) return false;
        if (!InfestGameManager.Instance.CanSpawnMore()) return false;
        if (cockroachPrefab == null)
        {
            Debug.LogWarning("[CockroachSpawner] No cockroach prefab assigned!");
            return false;
        }

        // Prefer a spot outside the player's FOV; if the player is watching
        // every free corner, fall back to any spot that still respects the
        // hard rules (player distance + roach spacing). A hard FOV rule
        // funneled every spawn into the small area behind the player and
        // recreated the clusters.
        Vector3? spawnPos = FindValidSpawnPosition();
        if (!spawnPos.HasValue)
        {
            spawnPos = FindAnySpawnPosition();
        }

        if (!spawnPos.HasValue)
            return false;

        // Final guard: make sure the chosen position sits on the floor
        Vector3 finalPos = spawnPos.Value;
        if (!TrySnapToFloor(finalPos, out finalPos))
            finalPos = new Vector3(spawnPos.Value.x, floorY + floorOffset, spawnPos.Value.z);

        // Spawn the cockroach
        Quaternion randomRotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
        GameObject roach = Instantiate(cockroachPrefab, finalPos, randomRotation);
        roach.name = $"Cockroach_{InfestGameManager.Instance.SpawnedCount}";

        aliveCockroaches.Add(roach);
        InfestGameManager.Instance.OnCockroachSpawned();

        Debug.Log($"[CockroachSpawner] Spawned {roach.name} at {spawnPos.Value}");
        return true;
    }

    /// <summary>
    /// Find a spawn position that is outside the player's field of view (L4D style).
    /// </summary>
    private Vector3? FindValidSpawnPosition()
    {
        if (spawnPoints.Count == 0) return null;

        // Shuffle and try spawn points
        List<int> indices = new List<int>();
        for (int i = 0; i < spawnPoints.Count; i++) indices.Add(i);
        ShuffleList(indices);

        foreach (int idx in indices)
        {
            Vector3 pos = spawnPoints[idx];

            // Check: outside player FOV
            if (playerCamera != null)
            {
                Vector3 toSpawn = (pos - playerCamera.position).normalized;
                float angle = Vector3.Angle(playerCamera.forward, toSpawn);

                if (angle < playerFOVAngle)
                    continue; // Inside player's view — skip
            }

            // Check: far enough from the player
            if (IsTooCloseToPlayer(pos))
                continue;

            // Check: not too close to existing cockroaches
            if (IsTooCloseToExisting(pos))
                continue;

            return pos;
        }

        return null;
    }

    /// <summary>
    /// HARD rule: no roach may materialize near the player (horizontal
    /// distance — the HMD sits ~1.5m above floor-level spawn points).
    /// </summary>
    private bool IsTooCloseToPlayer(Vector3 pos)
    {
        if (playerCamera == null) return false;

        Vector3 toPos = pos - playerCamera.position;
        float horizDist = new Vector2(toPos.x, toPos.z).magnitude;
        return horizDist < minDistanceFromPlayer;
    }

    /// <summary>
    /// Fallback: any spawn position regardless of FOV. Player distance and
    /// roach spacing remain HARD rules here — only the FOV preference drops.
    /// </summary>
    private Vector3? FindAnySpawnPosition()
    {
        if (spawnPoints.Count == 0) return null;

        List<int> indices = new List<int>();
        for (int i = 0; i < spawnPoints.Count; i++) indices.Add(i);
        ShuffleList(indices);

        foreach (int idx in indices)
        {
            Vector3 pos = spawnPoints[idx];

            if (IsTooCloseToPlayer(pos))
                continue;

            if (IsTooCloseToExisting(pos))
                continue;

            return pos;
        }

        // Every usable spot is crowded or at the player's feet — skip this
        // spawn tick instead of breaking the rules
        return null;
    }

    private bool IsTooCloseToExisting(Vector3 pos)
    {
        float separation = Mathf.Max(minDistanceBetween, minSpawnSeparation);
        foreach (var roach in aliveCockroaches)
        {
            if (roach == null) continue;
            if (Vector3.Distance(roach.transform.position, pos) < separation)
                return true;
        }
        return false;
    }

    private void ShuffleList<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            T temp = list[i];
            list[i] = list[j];
            list[j] = temp;
        }
    }
}
