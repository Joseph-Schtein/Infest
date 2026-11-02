using System.Collections.Generic;
using UnityEngine;
using Meta.XR.MRUtilityKit;

/// <summary>
/// Furniture-based cover queries for the cockroach AI, built on MRUK scene data.
///
/// Works purely on the MRUK anchor volume bounds (oriented boxes) — no physics
/// colliders required, so it behaves the same whether or not the EffectMesh
/// building block has colliders enabled.
///
///   - IsSegmentBlocked(a, b): does the line between two points pass through
///     any furniture volume? Used both for hiding-spot validation and for the
///     "is the roach visible to the player / reachable by the flashlight" test.
///   - TryFindHidingSpot(...): find a point on the floor just behind a piece
///     of furniture, on the far side from the threat (player/flashlight).
/// </summary>
public static class FurnitureCover
{
    /// <summary>How often the furniture anchor list is re-read from MRUK.</summary>
    private const float CacheRefreshInterval = 5f;

    /// <summary>Gap between the furniture surface and the hiding spot.</summary>
    private const float HideSpotMargin = 0.08f;

    /// <summary>
    /// The last bit of the segment (nearest the target) that is ignored in the
    /// occlusion test. Roaches spawn/walk right against furniture bases, so a
    /// segment ending at the roach can graze the furniture's own volume — that
    /// graze must not count as "the furniture is between us and the roach".
    /// </summary>
    private const float TargetClearance = 0.07f;

    private static readonly List<MRUKAnchor> furniture = new List<MRUKAnchor>();
    private static readonly List<MRUKAnchor> walls = new List<MRUKAnchor>();
    private static readonly List<Vector3> roomOutline = new List<Vector3>();
    private static MRUKRoom cachedRoom;
    private static float nextCacheRefresh = -1f;

    /// <summary>How far along each wall from a corner spots are offered.</summary>
    private static readonly float[] CornerSteps = { 0.3f, 0.7f };

    // ─── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// True if the segment from <paramref name="from"/> to <paramref name="to"/>
    /// passes through any furniture volume (i.e. the furniture occludes it).
    /// </summary>
    public static bool IsSegmentBlocked(Vector3 from, Vector3 to)
    {
        RefreshCacheIfNeeded();

        // Trim the segment just short of the target (see TargetClearance)
        Vector3 dir = to - from;
        float len = dir.magnitude;
        if (len > TargetClearance)
            to = from + dir * ((len - TargetClearance) / len);

        for (int i = 0; i < furniture.Count; i++)
        {
            var anchor = furniture[i];
            if (anchor == null) continue;
            if (SegmentIntersectsVolume(anchor, from, to))
                return true;
        }

        // Walls occlude too — a roach on the far side of a wall (or a light
        // shining from another room) must not see / be seen through it
        for (int i = 0; i < walls.Count; i++)
        {
            var wall = walls[i];
            if (wall == null) continue;
            if (SegmentIntersectsWall(wall, from, to))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True if the point lies inside any furniture volume (the MRUK prism of
    /// a desk, couch, table...). Roaches must never spawn or walk in there.
    /// </summary>
    public static bool IsInsideFurniture(Vector3 point)
    {
        RefreshCacheIfNeeded();

        for (int i = 0; i < furniture.Count; i++)
        {
            var anchor = furniture[i];
            if (anchor == null) continue;

            Bounds vb = anchor.VolumeBounds.Value;
            Vector3 local = anchor.transform.InverseTransformPoint(point);
            if (vb.Contains(local)) return true;
        }
        return false;
    }

    /// <summary>
    /// Tries to find a floor-level point tucked behind a piece of furniture,
    /// on the opposite side from <paramref name="threatPos"/> (typically the
    /// player's head). The returned spot is verified to be occluded from the
    /// threat by the furniture volumes.
    /// </summary>
    public static bool TryFindHidingSpot(Vector3 roachPos, Vector3 threatPos, float maxDistance, out Vector3 spot)
    {
        RefreshCacheIfNeeded();

        spot = roachPos;
        float bestScore = float.MaxValue;
        bool found = false;

        for (int i = 0; i < furniture.Count; i++)
        {
            var anchor = furniture[i];
            if (anchor == null || !anchor.VolumeBounds.HasValue) continue;

            Bounds vb = anchor.VolumeBounds.Value;
            Vector3 center = anchor.transform.TransformPoint(vb.center);

            // Horizontal direction from the threat through the furniture center
            Vector3 away = center - threatPos;
            away.y = 0f;
            if (away.sqrMagnitude < 1e-4f) continue;
            away.Normalize();

            // Support extent of the oriented box along 'away' — how far the
            // furniture surface reaches in that direction from its center
            Vector3 ext = vb.extents;
            float extent = Mathf.Abs(Vector3.Dot(away, anchor.transform.right))   * ext.x
                         + Mathf.Abs(Vector3.Dot(away, anchor.transform.up))      * ext.y
                         + Mathf.Abs(Vector3.Dot(away, anchor.transform.forward)) * ext.z;

            // Lateral extent along the back edge, for spreading the spots —
            // a single deterministic spot behind the center made every roach
            // pile onto the exact same point (an easy multi-kill cluster)
            Vector3 side = Vector3.Cross(Vector3.up, away).normalized;
            float lateral = Mathf.Abs(Vector3.Dot(side, anchor.transform.right))   * ext.x
                          + Mathf.Abs(Vector3.Dot(side, anchor.transform.up))      * ext.y
                          + Mathf.Abs(Vector3.Dot(side, anchor.transform.forward)) * ext.z;

            // Try a few random spots along the back edge; each roach rolls
            // its own offsets, so a group spreads out along the furniture
            for (int j = 0; j < 3; j++)
            {
                float sideOffset = Random.Range(-0.8f, 0.8f) * lateral;
                Vector3 candidate = center + away * (extent + HideSpotMargin) + side * sideOffset;
                candidate.y = roachPos.y; // stay on the roach's floor level

                float distFromRoach = Vector3.Distance(roachPos, candidate);
                if (distFromRoach > maxDistance) continue;

                // Never hide outside the room — furniture pushed against a wall
                // would otherwise send the roach through/behind that wall
                if (cachedRoom != null && !cachedRoom.IsPositionInRoom(candidate, testVerticalBounds: false))
                    continue;

                // ...and never at a spot that sits inside a neighboring prism
                if (IsInsideFurniture(candidate + Vector3.up * 0.03f))
                    continue;

                // The spot only counts as cover if the furniture actually blocks
                // the threat's view of it
                if (!IsSegmentBlocked(threatPos, candidate + Vector3.up * 0.03f)) continue;

                // Prefer nearby spots; penalize spots that force the roach to run
                // toward the threat to reach them
                Vector3 toCandidate = candidate - roachPos;
                Vector3 toThreat = threatPos - roachPos;
                toCandidate.y = 0f;
                toThreat.y = 0f;
                float towardThreatPenalty = 0f;
                if (toCandidate.sqrMagnitude > 1e-4f && toThreat.sqrMagnitude > 1e-4f)
                    towardThreatPenalty = Mathf.Max(0f, Vector3.Dot(toCandidate.normalized, toThreat.normalized)) * 2f;

                float score = distFromRoach + towardThreatPenalty;
                if (score < bestScore)
                {
                    bestScore = score;
                    spot = candidate;
                    found = true;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Finds the nearest spot hugging a wall face or a furniture base within
    /// <paramref name="maxDistance"/> (horizontal). Roaches are thigmotactic —
    /// they rest against edges and never out in the middle of open floor. The
    /// returned spot sits <paramref name="margin"/> out from the surface, on
    /// the caller's own floor level, inside the room and clear of every
    /// furniture prism.
    /// </summary>
    public static bool TryFindEdgeSpot(Vector3 fromPos, float maxDistance, float margin, out Vector3 spot)
    {
        RefreshCacheIfNeeded();

        spot = fromPos;
        float bestDist = float.MaxValue;
        bool found = false;

        // ── Wall faces: slide along the wall to the closest point on it,
        //    then step off its face toward whichever side the roach is on
        for (int i = 0; i < walls.Count; i++)
        {
            var wall = walls[i];
            if (wall == null || !wall.PlaneRect.HasValue) continue;

            Rect r = wall.PlaneRect.Value;
            Vector3 local = wall.transform.InverseTransformPoint(fromPos);

            float x = Mathf.Clamp(local.x, r.xMin, r.xMax);
            Vector3 onFace = wall.transform.TransformPoint(new Vector3(x, local.y, 0f));

            Vector3 outward = wall.transform.TransformDirection(
                new Vector3(0f, 0f, local.z >= 0f ? 1f : -1f));
            outward.y = 0f;
            if (outward.sqrMagnitude < 1e-6f) continue;

            Vector3 candidate = onFace + outward.normalized * margin;
            candidate.y = fromPos.y;

            ConsiderEdgeSpot(fromPos, candidate, maxDistance, ref bestDist, ref spot, ref found);
        }

        // ── Furniture bases: closest point on the prism's footprint, pushed
        //    straight out horizontally
        for (int i = 0; i < furniture.Count; i++)
        {
            var anchor = furniture[i];
            if (anchor == null || !anchor.VolumeBounds.HasValue) continue;

            Bounds vb = anchor.VolumeBounds.Value;
            Vector3 local = anchor.transform.InverseTransformPoint(fromPos);

            Vector3 clamped = new Vector3(
                Mathf.Clamp(local.x, vb.min.x, vb.max.x),
                local.y,
                Mathf.Clamp(local.z, vb.min.z, vb.max.z));

            Vector3 offset = local - clamped;
            offset.y = 0f;
            if (offset.sqrMagnitude < 1e-6f) continue; // standing on the footprint itself

            Vector3 outward = anchor.transform.TransformDirection(offset);
            outward.y = 0f;
            if (outward.sqrMagnitude < 1e-6f) continue;

            Vector3 candidate = anchor.transform.TransformPoint(clamped)
                              + outward.normalized * margin;
            candidate.y = fromPos.y;

            ConsiderEdgeSpot(fromPos, candidate, maxDistance, ref bestDist, ref spot, ref found);
        }

        return found;
    }

    /// <summary>
    /// Appends floor spots tucked against the walls around every corner of
    /// the room outline within <paramref name="maxDistance"/> (horizontal).
    ///
    /// A flat wall has no "behind" — there is no legal floor on its far side,
    /// so no spot along it can ever hide a roach. The only place a wall can
    /// actually break line of sight is where it TURNS: an alcove, a doorway
    /// recess, an L-shaped corner, a pillar. Those are seeded explicitly here
    /// because uniform random sampling almost never lands in one.
    ///
    /// This only PROPOSES places worth checking — whether a given spot really
    /// hides anything is left to the caller's own IsSegmentBlocked test, which
    /// already handles walls. In a plain rectangular room the proposals simply
    /// fail that test and the caller falls through, which is the correct
    /// outcome rather than a special case.
    /// </summary>
    public static void CollectCornerSpots(Vector3 fromPos, float maxDistance, float margin, List<Vector3> into)
    {
        RefreshCacheIfNeeded();
        if (roomOutline.Count < 3 || into == null) return;

        float reach = CornerSteps[CornerSteps.Length - 1] + margin;
        int n = roomOutline.Count;

        for (int i = 0; i < n; i++)
        {
            Vector3 v = roomOutline[i];
            v.y = fromPos.y;

            // Cheap reject for far-off corners before any geometry work
            if (new Vector2(v.x - fromPos.x, v.z - fromPos.z).magnitude > maxDistance + reach)
                continue;

            Vector3 a = FlatDir(roomOutline[(i - 1 + n) % n] - roomOutline[i]);
            Vector3 b = FlatDir(roomOutline[(i + 1) % n] - roomOutline[i]);
            if (a == Vector3.zero || b == Vector3.zero) continue;

            // The nook itself, pushed off the corner along the bisector
            Vector3 bis = a + b;
            if (bis.sqrMagnitude > 1e-6f)
                ConsiderCornerSpot(fromPos, v + bis.normalized * (margin * 2f), maxDistance, into);

            // ...and stepped along each wall leaving the corner, offset off
            // the face. BOTH sides are offered: which one points into the
            // room depends on whether this corner is convex or reflex, and
            // the wrong one simply fails the in-room test below.
            for (int e = 0; e < 2; e++)
            {
                Vector3 dir = e == 0 ? a : b;
                Vector3 perp = Vector3.Cross(Vector3.up, dir).normalized;

                for (int s = 0; s < CornerSteps.Length; s++)
                {
                    Vector3 baseP = v + dir * CornerSteps[s];
                    ConsiderCornerSpot(fromPos, baseP + perp * margin, maxDistance, into);
                    ConsiderCornerSpot(fromPos, baseP - perp * margin, maxDistance, into);
                }
            }
        }
    }

    /// <summary>Keeps a corner candidate that is in range, in the room and clear of furniture.</summary>
    private static void ConsiderCornerSpot(Vector3 fromPos, Vector3 candidate, float maxDistance, List<Vector3> into)
    {
        candidate.y = fromPos.y;

        if (new Vector2(candidate.x - fromPos.x, candidate.z - fromPos.z).magnitude > maxDistance) return;
        if (cachedRoom != null && !cachedRoom.IsPositionInRoom(candidate, testVerticalBounds: false)) return;
        if (IsInsideFurniture(candidate + Vector3.up * 0.03f)) return;

        into.Add(candidate);
    }

    /// <summary>Horizontal unit direction, or zero if the input is degenerate.</summary>
    private static Vector3 FlatDir(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude < 1e-6f ? Vector3.zero : v.normalized;
    }

    /// <summary>Keeps the closest valid edge candidate seen so far.</summary>
    private static void ConsiderEdgeSpot(Vector3 fromPos, Vector3 candidate, float maxDistance,
                                         ref float bestDist, ref Vector3 spot, ref bool found)
    {
        float d = new Vector2(candidate.x - fromPos.x, candidate.z - fromPos.z).magnitude;
        if (d > maxDistance || d >= bestDist) return;

        if (cachedRoom != null && !cachedRoom.IsPositionInRoom(candidate, testVerticalBounds: false))
            return;
        if (IsInsideFurniture(candidate + Vector3.up * 0.03f)) return;

        bestDist = d;
        spot = candidate;
        found = true;
    }

    // ─── Internals ──────────────────────────────────────────────────────────────

    private static void RefreshCacheIfNeeded()
    {
        if (Time.time < nextCacheRefresh) return;
        nextCacheRefresh = Time.time + CacheRefreshInterval;

        furniture.Clear();
        walls.Clear();
        roomOutline.Clear();

        var room = MRUK.Instance != null ? MRUK.Instance.GetCurrentRoom() : null;
        cachedRoom = room;
        if (room == null) return;

        // Floor corner ring, used to seed corner/alcove hiding spots
        var outline = room.GetRoomOutline();
        if (outline != null) roomOutline.AddRange(outline);

        foreach (var anchor in room.Anchors)
        {
            if (anchor == null) continue;

            // Walls go into their own list — they occlude but are not cover
            if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE))
            {
                if (anchor.PlaneRect.HasValue) walls.Add(anchor);
                continue;
            }

            if (!anchor.VolumeBounds.HasValue) continue;

            // Furniture only — floor/ceiling are not hiding spots, and the
            // global scan mesh spans the whole room (it would occlude
            // everything from everywhere)
            if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.FLOOR) ||
                anchor.HasAnyLabel(MRUKAnchor.SceneLabels.CEILING) ||
                anchor.HasAnyLabel(MRUKAnchor.SceneLabels.GLOBAL_MESH))
                continue;

            furniture.Add(anchor);
        }
    }

    /// <summary>
    /// Segment vs wall plane: MRUK wall anchors are rectangles on their local
    /// z = 0 plane. The segment crosses the wall if its endpoints are on
    /// opposite sides of that plane and the crossing point lies inside the
    /// wall's rectangle.
    /// </summary>
    private static bool SegmentIntersectsWall(MRUKAnchor wall, Vector3 a, Vector3 b)
    {
        Rect r = wall.PlaneRect.Value;
        Vector3 la = wall.transform.InverseTransformPoint(a);
        Vector3 lb = wall.transform.InverseTransformPoint(b);

        if (la.z > 0f == lb.z > 0f) return false; // both on the same side

        float t = la.z / (la.z - lb.z);
        float x = Mathf.Lerp(la.x, lb.x, t);
        float y = Mathf.Lerp(la.y, lb.y, t);
        return x >= r.xMin && x <= r.xMax && y >= r.yMin && y <= r.yMax;
    }

    /// <summary>
    /// Segment vs oriented box: transform the segment into the anchor's local
    /// space and run a standard slab test against the local-space bounds.
    /// </summary>
    private static bool SegmentIntersectsVolume(MRUKAnchor anchor, Vector3 a, Vector3 b)
    {
        Bounds vb = anchor.VolumeBounds.Value;
        Vector3 la = anchor.transform.InverseTransformPoint(a);
        Vector3 lb = anchor.transform.InverseTransformPoint(b);
        Vector3 d = lb - la;

        float tmin = 0f, tmax = 1f;
        for (int i = 0; i < 3; i++)
        {
            float start = la[i];
            float dir = d[i];
            float min = vb.min[i];
            float max = vb.max[i];

            if (Mathf.Abs(dir) < 1e-6f)
            {
                // Segment parallel to this slab — must already be inside it
                if (start < min || start > max) return false;
            }
            else
            {
                float t1 = (min - start) / dir;
                float t2 = (max - start) / dir;
                if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                tmin = Mathf.Max(tmin, t1);
                tmax = Mathf.Min(tmax, t2);
                if (tmin > tmax) return false;
            }
        }
        return true;
    }
}
