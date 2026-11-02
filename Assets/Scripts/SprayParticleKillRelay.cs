using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Forwards spray particle collisions to cockroaches.
/// Unity only delivers OnParticleCollision to the GameObject that owns the
/// ParticleSystem — which is a child of the spray can model, not the
/// GameObject holding SprayCanController. SprayCanController attaches this
/// relay to the particle system's GameObject at runtime.
/// </summary>
[RequireComponent(typeof(ParticleSystem))]
public class SprayParticleKillRelay : MonoBehaviour
{
    [HideInInspector] public float damagePerHit = 1f;

    private ParticleSystem ps;
    private List<ParticleCollisionEvent> collisionEvents = new List<ParticleCollisionEvent>();

    void Awake()
    {
        ps = GetComponent<ParticleSystem>();
    }

    void OnParticleCollision(GameObject other)
    {
        int numEvents = ps.GetCollisionEvents(other, collisionEvents);

        CockroachBehavior cockroach = other.GetComponent<CockroachBehavior>();
        if (cockroach == null)
            cockroach = other.GetComponentInParent<CockroachBehavior>();

        if (cockroach != null)
        {
            // Furniture/wall occlusion: the scanned room geometry has no
            // physics colliders, so particles fly straight through a couch.
            // If an MRUK furniture volume or wall sits between the nozzle and
            // the impact point, the spray couldn't really have reached — the
            // hit doesn't count. (Straight-segment test: a lobbed arc over a
            // low obstacle is sacrificed for not killing blind through cover.)
            Vector3 impact = numEvents > 0
                ? collisionEvents[0].intersection
                : cockroach.transform.position + Vector3.up * 0.02f;
            if (FurnitureCover.IsSegmentBlocked(transform.position, impact))
            {
                Debug.Log($"[SprayCan] Hit on {other.name} blocked by furniture/wall — ignored");
                return;
            }

            cockroach.TakeDamage(damagePerHit);
            Debug.Log($"[SprayCan] Hit cockroach {other.name}! ({numEvents} collisions)");
        }
    }
}
