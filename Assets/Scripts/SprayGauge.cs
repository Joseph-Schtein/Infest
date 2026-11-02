using UnityEngine;

/// <summary>
/// Grim world-space ammo gauge floating above the spray can: a thin dark
/// bar whose fill drains from toxic green to blood red as the spray budget
/// is spent, with an uneasy flicker when nearly empty. Created at runtime
/// by SprayCanController — no scene setup needed.
/// </summary>
public class SprayGauge : MonoBehaviour
{
    [Tooltip("Bar width in meters")]
    public float barWidth = 0.08f;

    [Tooltip("Bar height in meters")]
    public float barHeight = 0.012f;

    private Transform fill;
    private Renderer fillRenderer;
    private Camera cam;
    private float currentFill = 1f;
    private Color baseFillColor;
    private Transform anchor;      // tool this gauge hovers above
    private Vector3 anchorOffset;  // WORLD-space offset from the anchor

    /// <summary>
    /// Creates a gauge as a child of parent that hovers at parent's position
    /// plus worldOffset (world meters, re-applied every frame). Two traps
    /// this avoids, learned on the flashlight:
    ///  - parent scale: the flashlight FBX is scaled to ~0.0002, so a raw
    ///    child gauge is microns wide — localScale compensates;
    ///  - parent axes: the FBX's local axes are rotated, so a LOCAL offset
    ///    of "0.18 up" lands inside the body instead of above it — the
    ///    offset is therefore applied in world space in LateUpdate.
    /// </summary>
    public static SprayGauge Attach(Transform parent, Vector3 worldOffset)
    {
        var go = new GameObject(parent.name + "Gauge");
        go.transform.SetParent(parent, false);

        Vector3 ls = parent.lossyScale;
        go.transform.localScale = new Vector3(
            Mathf.Approximately(ls.x, 0f) ? 1f : 1f / ls.x,
            Mathf.Approximately(ls.y, 0f) ? 1f : 1f / ls.y,
            Mathf.Approximately(ls.z, 0f) ? 1f : 1f / ls.z);
        go.transform.position = parent.position + worldOffset;

        var gauge = go.AddComponent<SprayGauge>();
        gauge.anchor = parent;
        gauge.anchorOffset = worldOffset;
        return gauge;
    }

    void Start()
    {
        BuildVisuals();
    }

    void LateUpdate()
    {
        // Float straight above the tool in WORLD space, no matter how the
        // tool model is rotated or how the hand tilts it
        if (anchor != null)
            transform.position = anchor.position + anchorOffset;

        // Billboard toward the player
        if (cam == null) cam = Camera.main;
        if (cam != null)
        {
            transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position);
        }

        // Unsettling flicker when the can is nearly spent
        if (fillRenderer != null && currentFill > 0f && currentFill < 0.2f)
        {
            float flicker = 0.65f + 0.35f * Mathf.PerlinNoise(Time.time * 9f, 0f);
            SetColor(fillRenderer, baseFillColor * flicker);
        }
    }

    /// <summary>Update the gauge fill. fill01 is 0..1 of the spray budget remaining.</summary>
    public void SetFill(float fill01)
    {
        currentFill = Mathf.Clamp01(fill01);

        if (fill != null)
        {
            float w = Mathf.Max(0.001f, barWidth * currentFill);
            fill.localScale = new Vector3(w, barHeight * 0.7f, 1f);
            fill.localPosition = new Vector3(-barWidth / 2f + w / 2f, 0f, -0.001f);
        }

        if (fillRenderer != null)
        {
            // Toxic green when full, draining down to dark blood red
            baseFillColor = Color.Lerp(new Color(0.45f, 0.05f, 0.05f), new Color(0.25f, 0.65f, 0.2f), currentFill);
            SetColor(fillRenderer, baseFillColor);
        }
    }

    private void BuildVisuals()
    {
        // Track: near-black backing
        CreateQuad("Track", new Vector3(0f, 0f, 0f),
            new Vector3(barWidth, barHeight, 1f), new Color(0.05f, 0.05f, 0.06f));

        // Fill
        var fillQuad = CreateQuad("Fill", new Vector3(0f, 0f, -0.001f),
            new Vector3(barWidth, barHeight * 0.7f, 1f), new Color(0.25f, 0.65f, 0.2f));
        fill = fillQuad.transform;
        fillRenderer = fillQuad.GetComponent<Renderer>();

        SetFill(1f);
    }

    private GameObject CreateQuad(string name, Vector3 localPos, Vector3 localScale, Color color)
    {
        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = name;
        Destroy(quad.GetComponent<Collider>());
        quad.transform.SetParent(transform, false);
        quad.transform.localPosition = localPos;
        quad.transform.localScale = localScale;

        var sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Unlit/Color");
        var rend = quad.GetComponent<MeshRenderer>();
        rend.sharedMaterial = new Material(sh);
        SetColor(rend, color);
        rend.sortingOrder = 900;
        return quad;
    }

    private static void SetColor(Renderer rend, Color c)
    {
        var mat = rend.sharedMaterial;
        if (mat == null) return;
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);
    }
}
