using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using Pathfinding;
using Pathfinding.Graphs.Grid; // ColliderType lives here since A* 5.x
using Meta.XR.MRUtilityKit;

/// <summary>
/// Bakes an A* Pathfinding GridGraph at runtime on the MRUK scene geometry.
/// The grid is placed on the floor surface so cockroaches can pathfind on it.
/// Requires an AstarPath component in the scene (auto-created if missing).
/// </summary>
public class RuntimeNavMeshBaker : MonoBehaviour
{
    [Header("Grid Settings")]
    [Tooltip("Node size for the A* grid (smaller = more precise but slower)")]
    public float nodeSize = 0.1f;

    [Tooltip("Extra padding around the room bounds for the grid")]
    public float padding = 0.5f;

    [Tooltip("Layer mask for collision testing (should include scene mesh layers)")]
    public LayerMask collisionMask = ~0;

    [Tooltip("Height above the floor to test for obstacles")]
    public float collisionHeight = 0.3f;

    [Tooltip("Clearance (m) kept between walkable nodes and walls/furniture. " +
             "Blocks the outermost node ring(s) along every boundary, so " +
             "wall-hugging routes ride at antenna distance instead of " +
             "clipping the roach body into the geometry, and corner turns " +
             "happen a node earlier instead of shaving through the corner. " +
             "0 = off.")]
    public float wallClearance = 0.1f;

    [Header("Open-Floor Penalty (thigmotaxis)")]
    [Tooltip("How much more expensive deep open floor is than routes hugging walls/furniture. " +
             "2 = crossing open floor costs like a wall-hugging route 3x as long. 0 = disabled.")]
    public float openFloorPenalty = 2f;

    [Tooltip("Distance from the nearest wall/furniture edge that stays penalty-free")]
    public float edgeComfortDistance = 0.25f;

    [Tooltip("Penalty ramps up linearly from edgeComfortDistance to here; full penalty beyond")]
    public float openFloorDistance = 1.0f;

    [Header("Events")]
    public UnityEvent OnNavMeshReady;

    private AstarPath astarPath;
    private bool isBaked = false;

    /// <summary>
    /// Call this after the MRUK room is loaded.
    /// Typically connected to RoomScanPrompt.OnRoomReady.
    /// </summary>
    public void BakeNavMesh()
    {
        if (isBaked)
        {
            Debug.Log("[NavMeshBaker] Already baked, re-scanning...");
        }

        var room = MRUK.Instance?.GetCurrentRoom();
        if (room == null)
        {
            Debug.LogWarning("[NavMeshBaker] No MRUK room found. Creating fallback grid.");
            CreateFallbackGrid();
            return;
        }

        SetupAstarPath();
        ConfigureGridFromRoom(room);

        Debug.Log("[NavMeshBaker] Scanning A* grid graph...");
        astarPath.Scan();

        // The grid covers the room's rectangular bounds (plus padding), so
        // it reaches beyond the scanned walls — without this pass those
        // outside nodes are walkable and fleeing roaches path through walls.
        ConfineGraphToRoom(room);

        isBaked = true;
        Debug.Log("[NavMeshBaker] A* NavMesh bake complete!");
        OnNavMeshReady?.Invoke();
    }

    /// <summary>
    /// Marks every grid node that lies outside the scanned room outline OR
    /// inside a furniture volume prism as unwalkable, then rebuilds node
    /// connections. Keeps all pathfinding strictly inside the room and out
    /// of desks/tables — scanned furniture has no physics colliders, so the
    /// grid's collision test alone can't detect it.
    /// </summary>
    private void ConfineGraphToRoom(MRUKRoom room)
    {
        astarPath.AddWorkItem(new AstarWorkItem(() =>
        {
            foreach (var graph in astarPath.data.graphs)
            {
                var gg = graph as GridGraph;
                if (gg == null || gg.nodes == null) continue;

                // Cause-split counters: after the 2026-07-20 zero-walkable
                // incident, the bake must PROVE how many nodes survive —
                // "walkable after: 0" is the one-line smoking gun for any
                // future all-paths-fail round.
                int walkableBefore = 0, blockedOutside = 0, blockedFurniture = 0;
                for (int i = 0; i < gg.nodes.Length; i++)
                {
                    var node = gg.nodes[i];
                    if (node == null || !node.Walkable) continue;
                    walkableBefore++;

                    Vector3 p = (Vector3)node.position;
                    if (!room.IsPositionInRoom(p, testVerticalBounds: false))
                    {
                        node.Walkable = false;
                        blockedOutside++;
                    }
                    // Two sample heights: floor+2cm catches normal prisms,
                    // floor+25cm catches prisms whose scanned volume bottom
                    // FLOATS above the floor (scan drift) — without the
                    // second sample those floor nodes count as open floor
                    // and roaches walk into the furniture's footprint.
                    else if (FurnitureCover.IsInsideFurniture(p + Vector3.up * 0.02f)
                          || FurnitureCover.IsInsideFurniture(p + Vector3.up * 0.25f))
                    {
                        node.Walkable = false;
                        blockedFurniture++;
                    }
                }

                // Clearance erosion: peel the outermost walkable ring(s) off
                // every boundary (walls AND furniture) so no route can hug
                // closer than wallClearance. Two-phase per ring — collect
                // then apply — or a single sweep would cascade through
                // several rings in scan order. Runs BEFORE the penalty BFS,
                // which then measures its comfort band from the eroded edge.
                int eroded = 0;
                int rings = Mathf.Clamp(Mathf.RoundToInt(wallClearance / gg.nodeSize), 0, 3);
                var toErode = new List<int>();
                for (int ring = 0; ring < rings; ring++)
                {
                    toErode.Clear();
                    for (int z = 0; z < gg.depth; z++)
                    {
                        for (int x = 0; x < gg.width; x++)
                        {
                            int i = z * gg.width + x;
                            var node = gg.nodes[i];
                            if (node == null || !node.Walkable) continue;

                            bool boundary = x == 0 || z == 0 || x == gg.width - 1 || z == gg.depth - 1;
                            for (int dz = -1; dz <= 1 && !boundary; dz++)
                                for (int dx = -1; dx <= 1 && !boundary; dx++)
                                {
                                    if (dx == 0 && dz == 0) continue;
                                    var n = gg.nodes[(z + dz) * gg.width + (x + dx)];
                                    if (n == null || !n.Walkable) boundary = true;
                                }

                            if (boundary) toErode.Add(i);
                        }
                    }
                    for (int k = 0; k < toErode.Count; k++)
                        gg.nodes[toErode[k]].Walkable = false;
                    eroded += toErode.Count;
                }

                // Refresh connections so paths can't jump over the border
                for (int z = 0; z < gg.depth; z++)
                    for (int x = 0; x < gg.width; x++)
                        gg.CalculateConnectionsForCellAndNeighbours(x, z);

                int walkableAfter = walkableBefore - blockedOutside - blockedFurniture - eroded;
                Debug.Log($"[NavMeshBaker] Confined graph: walkable {walkableBefore} -> {walkableAfter} " +
                          $"(blocked {blockedOutside} outside room, {blockedFurniture} inside furniture, " +
                          $"{eroded} eroded for {wallClearance}m wall clearance, of {gg.nodes.Length} total)");
                if (walkableAfter == 0)
                    Debug.LogWarning("[NavMeshBaker] GRAPH HAS ZERO WALKABLE NODES — " +
                                     "every path will fail and roaches will use the straight-line fallback!");

                // Must run AFTER confinement — the penalty gradient is
                // measured from the final walkable/unwalkable boundary
                ApplyEdgePenalties(gg);
            }
        }));
        astarPath.FlushWorkItems();
    }

    /// <summary>
    /// Cockroach thigmotaxis baked into the graph: every walkable node gets
    /// a penalty growing with its distance from the nearest unwalkable edge
    /// (wall, furniture prism or room border), so EVERY path — flee, vanish,
    /// breakout, idle dart — natively prefers routes that hug cover over
    /// crossing open floor. A* adds a node's Penalty once per node ENTERED
    /// (GetConnectionCost, 1000 = the cost of walking 1m), so the full
    /// penalty is scaled by nodeSize to make the cost per METER independent
    /// of grid resolution: deep open floor costs (1 + openFloorPenalty)x the
    /// equivalent wall-hugging distance. Runs inside the confinement work
    /// item, on the FINAL walkability state.
    /// </summary>
    private void ApplyEdgePenalties(GridGraph gg)
    {
        if (openFloorPenalty <= 0f) return; // bisect knob: 0 = off

        int w = gg.width, d = gg.depth;
        var nodes = gg.nodes;
        if (nodes == null || nodes.Length < w * d) return;

        // Multi-source BFS over the grid (8-connected, distance in node
        // steps) seeded at every walkable node touching an unwalkable
        // neighbour or the grid border.
        var steps = new int[w * d];
        for (int i = 0; i < steps.Length; i++) steps[i] = -1;
        var queue = new Queue<int>();

        for (int z = 0; z < d; z++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = z * w + x;
                var node = nodes[i];
                if (node == null || !node.Walkable) continue;

                bool atEdge = x == 0 || z == 0 || x == w - 1 || z == d - 1;
                for (int dz = -1; dz <= 1 && !atEdge; dz++)
                    for (int dx = -1; dx <= 1 && !atEdge; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        var n = nodes[(z + dz) * w + (x + dx)];
                        if (n == null || !n.Walkable) atEdge = true;
                    }

                if (atEdge)
                {
                    steps[i] = 0;
                    queue.Enqueue(i);
                }
            }
        }

        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int x = i % w, z = i / w;
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    int nx = x + dx, nz = z + dz;
                    if (nx < 0 || nz < 0 || nx >= w || nz >= d) continue;
                    int ni = nz * w + nx;
                    if (steps[ni] != -1) continue;
                    var n = nodes[ni];
                    if (n == null || !n.Walkable) continue;
                    steps[ni] = steps[i] + 1;
                    queue.Enqueue(ni);
                }
            }
        }

        // Distance in steps → meters → penalty along the comfort/ramp curve
        float ramp = Mathf.Max(0.01f, openFloorDistance - edgeComfortDistance);
        uint fullPenalty = (uint)Mathf.RoundToInt(openFloorPenalty * 1000f * gg.nodeSize);
        int penalized = 0;

        for (int i = 0; i < steps.Length; i++)
        {
            var node = nodes[i];
            if (node == null || !node.Walkable) continue;
            float meters = steps[i] < 0 ? openFloorDistance : steps[i] * gg.nodeSize;
            float t = Mathf.Clamp01((meters - edgeComfortDistance) / ramp);
            uint p = (uint)Mathf.RoundToInt(t * fullPenalty);
            node.Penalty = p;
            if (p > 0) penalized++;
        }

        Debug.Log($"[NavMeshBaker] Edge penalties: {penalized} open-floor nodes penalized, " +
                  $"full penalty {fullPenalty}/node beyond {openFloorDistance}m from cover");
    }

    private void SetupAstarPath()
    {
        // Find or create AstarPath
        astarPath = FindAnyObjectByType<AstarPath>();
        if (astarPath == null)
        {
            var go = new GameObject("A*");
            astarPath = go.AddComponent<AstarPath>();
            Debug.Log("[NavMeshBaker] Created AstarPath GameObject");
        }
    }

    private void ConfigureGridFromRoom(MRUKRoom room)
    {
        // Get room bounds from the floor anchor. High Fidelity scenes can report
        // several floors, but this baker produces one flat grid at a single Y
        // (heightCheck is off below), so only the first floor is usable.
        var floorAnchor = room.FloorAnchors.Count > 0 ? room.FloorAnchors[0] : null;
        if (floorAnchor == null)
        {
            Debug.LogWarning("[NavMeshBaker] No floor anchor found!");
            CreateFallbackGrid();
            return;
        }

        if (room.FloorAnchors.Count > 1)
        {
            Debug.LogWarning($"[NavMeshBaker] Room reports {room.FloorAnchors.Count} floor anchors — " +
                             "baking on the first one only; roaches will not path across the others.");
        }

        // Get room outline to determine bounds
        var outline = room.GetRoomOutline();
        if (outline == null || outline.Count == 0)
        {
            Debug.LogWarning("[NavMeshBaker] Room outline is empty!");
            CreateFallbackGrid();
            return;
        }

        // Calculate room bounds from outline
        Vector3 min = outline[0];
        Vector3 max = outline[0];
        foreach (var point in outline)
        {
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }

        Vector3 center = (min + max) / 2f;
        Vector3 size = max - min;

        // Floor Y position
        float floorY = floorAnchor.GetAnchorCenter().y;
        center.y = floorY;

        Debug.Log($"[NavMeshBaker] Room bounds — Center: {center}, Size: {size}");

        // Clear existing graphs and create a new grid graph
        var gridGraphData = astarPath.data;

        GridGraph gridGraph;
        if (gridGraphData.graphs != null && gridGraphData.graphs.Length > 0 && gridGraphData.graphs[0] is GridGraph existing)
        {
            gridGraph = existing;
        }
        else
        {
            gridGraph = gridGraphData.AddGraph(typeof(GridGraph)) as GridGraph;
        }

        // Configure the grid graph
        int widthNodes = Mathf.CeilToInt((size.x + padding * 2) / nodeSize);
        int depthNodes = Mathf.CeilToInt((size.z + padding * 2) / nodeSize);

        // Clamp to reasonable limits for performance
        widthNodes = Mathf.Clamp(widthNodes, 10, 500);
        depthNodes = Mathf.Clamp(depthNodes, 10, 500);

        gridGraph.SetDimensions(widthNodes, depthNodes, nodeSize);
        gridGraph.center = center;
        gridGraph.rotation = Vector3.zero;

        // NO physics collision test (2026-07-20). The only collider in an
        // MRUK scene is the scanned global-mesh/floor, and its bumps
        // intersect the test capsule at EVERY interior node — device logs
        // showed the scan blocking the whole room interior, confinement
        // then blocking the exterior, leaving ZERO walkable nodes: every
        // path failed ("couldn't find node close to the start point") and
        // all roaches silently ran on the graph-less fallback. Walls and
        // furniture are what actually shape the graph, and ConfineGraphToRoom
        // handles both from MRUK data — physics has nothing true to add.
        gridGraph.collision.collisionCheck = false;
        gridGraph.collision.use2D = false;
        gridGraph.collision.type = ColliderType.Capsule;
        gridGraph.collision.diameter = nodeSize * 0.8f;
        gridGraph.collision.height = collisionHeight;
        gridGraph.collision.mask = ObstacleMask();

        // NO height testing — the grid plane is already placed at the MRUK
        // floor Y. Height testing requires a physics collider on the floor,
        // which the scanned room often doesn't have; with it enabled every
        // node fails the check and the whole graph becomes unwalkable
        // (roaches then play the flee animation but never move).
        gridGraph.collision.heightCheck = false;

        // Connection settings
        gridGraph.neighbours = NumNeighbours.Eight;
        gridGraph.cutCorners = true;
        gridGraph.maxStepHeight = 0.05f; // Cockroaches stay on flat floor
        gridGraph.maxSlope = 30f;

        Debug.Log($"[NavMeshBaker] Grid configured: {widthNodes}x{depthNodes} nodes, nodeSize={nodeSize}");
    }

    /// <summary>
    /// Collision mask for graph obstacle testing, with the Cockroach layer
    /// removed so roach colliders can't carve holes in their own graph.
    /// </summary>
    private int ObstacleMask()
    {
        int mask = collisionMask.value;
        int cockroachLayer = LayerMask.NameToLayer("Cockroach");
        if (cockroachLayer < 0) cockroachLayer = 7; // project default
        return mask & ~(1 << cockroachLayer);
    }

    private void CreateFallbackGrid()
    {
        SetupAstarPath();

        var gridGraphData = astarPath.data;
        GridGraph gridGraph;
        if (gridGraphData.graphs != null && gridGraphData.graphs.Length > 0 && gridGraphData.graphs[0] is GridGraph existing)
        {
            gridGraph = existing;
        }
        else
        {
            gridGraph = gridGraphData.AddGraph(typeof(GridGraph)) as GridGraph;
        }

        // Create a default 10m x 10m grid centered at origin
        gridGraph.SetDimensions(100, 100, nodeSize);
        gridGraph.center = Vector3.zero;

        gridGraph.collision.use2D = false;
        gridGraph.collision.type = ColliderType.Capsule;
        gridGraph.collision.diameter = nodeSize * 0.8f;
        gridGraph.collision.height = collisionHeight;
        gridGraph.collision.mask = ObstacleMask();
        gridGraph.collision.heightCheck = false; // no floor collider guaranteed — see ConfigureGridFromRoom
        gridGraph.neighbours = NumNeighbours.Eight;

        Debug.Log("[NavMeshBaker] Fallback grid configured");
        astarPath.Scan();

        isBaked = true;
        OnNavMeshReady?.Invoke();
    }
}
