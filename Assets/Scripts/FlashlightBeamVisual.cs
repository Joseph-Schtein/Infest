using UnityEngine;

/// <summary>
/// Attaches a visible volumetric-style light beam cone to the flashlight Spotlight.
///
/// HOW IT WORKS:
///   - Generates a procedural cone mesh matching the Light's SpotAngle and Range.
///   - Uses URP Unlit shader with proper additive blending for a glowing beam look.
///   - No vertex-color dependency - single solid color with tunable opacity.
///   - Rebuilds mesh only when SpotAngle/Range change, not every frame.
///
/// SETUP:
///   Attach to your "Spot Light" GameObject (the one with the Light component).
///   Beam material is auto-created. Tune Beam Color and Beam Opacity in Inspector.
/// </summary>
[RequireComponent(typeof(Light))]
public class FlashlightBeamVisual : MonoBehaviour
{
    [Header("Beam Appearance")]
    [Tooltip("Color tint of the beam.")]
    public Color beamColor = new Color(1f, 0.95f, 0.75f);

    [Range(0f, 1f)]
    [Tooltip("Opacity of the beam cone. 0 = invisible, 1 = fully solid.")]
    public float beamOpacity = 0.4f;

    [Header("Cone Quality")]
    [Range(8, 48)]
    [Tooltip("Number of sides on the cone.")]
    public int coneSides = 20;

    // -- Internals ------------------------------------------------------------

    private Light        _spotLight;
    private MeshFilter   _meshFilter;
    private MeshRenderer _meshRenderer;
    private Mesh         _mesh;
    private Material     _material;

    private float _lastSpotAngle = -1f;
    private float _lastRange     = -1f;
    private float _lastOpacity   = -1f;
    private Color _lastColor     = Color.clear;

    // -- Lifecycle ------------------------------------------------------------

    void Awake()
    {
        _spotLight = GetComponent<Light>();

        var beamGO = new UnityEngine.GameObject("_BeamMesh");
        beamGO.transform.SetParent(transform, false);
        beamGO.transform.localPosition = UnityEngine.Vector3.zero;
        beamGO.transform.localRotation = UnityEngine.Quaternion.identity;
        beamGO.transform.localScale    = UnityEngine.Vector3.one;

        _meshFilter   = beamGO.AddComponent<MeshFilter>();
        _meshRenderer = beamGO.AddComponent<MeshRenderer>();
        _mesh         = new Mesh { name = "FlashlightCone" };
        _meshFilter.sharedMesh = _mesh;

        _material = CreateBeamMaterial();
        _meshRenderer.sharedMaterial    = _material;
        _meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _meshRenderer.receiveShadows    = false;

        BuildCone();
    }

    void OnDestroy()
    {
        if (_material != null) Destroy(_material);
        if (_mesh     != null) Destroy(_mesh);
    }

    void LateUpdate()
    {
        if (_spotLight == null) return;

        // The visible cone must die with the light — trigger released or
        // battery dead both disable the Light component
        if (_meshRenderer != null && _meshRenderer.enabled != _spotLight.enabled)
            _meshRenderer.enabled = _spotLight.enabled;
        if (!_spotLight.enabled) return;

        bool needsRebuild = !Mathf.Approximately(_spotLight.spotAngle, _lastSpotAngle)
                         || !Mathf.Approximately(_spotLight.range,     _lastRange);
        if (needsRebuild)
            BuildCone();

        bool needsColor = !Mathf.Approximately(beamOpacity, _lastOpacity) || beamColor != _lastColor;
        if (needsColor)
            ApplyColor();
    }

    // -- Private Methods ------------------------------------------------------

    private void BuildCone()
    {
        if (_spotLight.type != LightType.Spot) return;

        float halfAngle = _spotLight.spotAngle * 0.5f * Mathf.Deg2Rad;
        float length    = _spotLight.range;
        float radius    = Mathf.Tan(halfAngle) * length;
        int   sides     = coneSides;

        // Vertices: [0] = tip at origin, [1..sides] = ring, [sides+1] = seam close
        int totalVerts = 1 + sides + 1;
        var verts = new Vector3[totalVerts];
        var uvs   = new Vector2[totalVerts];

        verts[0] = Vector3.zero;
        uvs[0]   = new Vector2(0.5f, 0f);

        for (int i = 0; i < sides; i++)
        {
            float a = (i / (float)sides) * Mathf.PI * 2f;
            verts[i + 1] = new Vector3(Mathf.Sin(a) * radius, Mathf.Cos(a) * radius, length);
            uvs[i + 1]   = new Vector2(i / (float)sides, 1f);
        }
        verts[sides + 1] = verts[1];
        uvs[sides + 1]   = new Vector2(1f, 1f);

        // Side triangles (open cone - no base cap)
        var tris = new int[sides * 3];
        for (int i = 0; i < sides; i++)
        {
            tris[i * 3 + 0] = 0;
            tris[i * 3 + 1] = i + 1;
            tris[i * 3 + 2] = i + 2;
        }

        _mesh.Clear();
        _mesh.vertices  = verts;
        _mesh.uv        = uvs;
        _mesh.triangles = tris;
        _mesh.RecalculateNormals();
        _mesh.RecalculateBounds();

        _lastSpotAngle = _spotLight.spotAngle;
        _lastRange     = _spotLight.range;

        ApplyColor();
    }

    private void ApplyColor()
    {
        if (_material == null) return;
        var c = new Color(beamColor.r, beamColor.g, beamColor.b, beamOpacity);
        if (_material.HasProperty("_BaseColor")) _material.SetColor("_BaseColor", c);
        if (_material.HasProperty("_Color"))     _material.SetColor("_Color",     c);
        _lastOpacity = beamOpacity;
        _lastColor   = beamColor;
    }

    /// <summary>
    /// Creates a URP-compatible additive transparent material.
    /// </summary>
    private Material CreateBeamMaterial()
    {
        // Use URP Unlit - confirmed available in this project
        Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Unlit/Transparent");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        if (sh == null)
        {
            UnityEngine.Debug.LogError("[FlashlightBeamVisual] No suitable shader found! Assign a Material manually.");
            sh = Shader.Find("Standard");
        }

        var mat = new Material(sh) { name = "FlashlightBeamMat" };

        // URP: mark as Transparent surface
        if (mat.HasProperty("_Surface"))   mat.SetFloat("_Surface",   1f); // 1 = Transparent
        if (mat.HasProperty("_Blend"))     mat.SetFloat("_Blend",     0f); // 0 = Alpha
        if (mat.HasProperty("_AlphaClip")) mat.SetFloat("_AlphaClip", 0f);

        // Override to Additive blending: src*alpha + dst*1
        mat.SetOverrideTag("RenderType", "Transparent");
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
        mat.SetInt("_ZWrite",   0);
        mat.SetInt("_Cull",     (int)UnityEngine.Rendering.CullMode.Off); // visible from all angles
        mat.SetInt("_ZTest",    (int)UnityEngine.Rendering.CompareFunction.LessEqual);

        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.DisableKeyword("_ALPHATEST_ON");
        mat.EnableKeyword("_ALPHABLEND_ON");
        mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");

        // Render BEFORE the roaches (they sit at the default Transparent
        // queue and write no depth): at Transparent+50 the additive cone drew
        // over every roach inside the beam and tinted it warm/yellow.
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent - 2;

        var c = new Color(beamColor.r, beamColor.g, beamColor.b, beamOpacity);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
        if (mat.HasProperty("_Color"))     mat.SetColor("_Color",     c);

        return mat;
    }
}
