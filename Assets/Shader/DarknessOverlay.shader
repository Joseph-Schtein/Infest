Shader "Custom/DarknessOverlay"
{
    // ──────────────────────────────────────────────────────────────────────────
    // Fullscreen darkness overlay with world-space cone reveal ("flashlight beam").
    //
    // HOW IT WORKS:
    //   1. Samples the rendered scene color from _BlitTexture (URP fullscreen blit).
    //   2. For each pixel, reconstructs the camera ray direction (NO depth needed).
    //   3. Samples the cone test at multiple depths along the ray.
    //   4. Decomposes each sample into axial + perpendicular distance from the cone.
    //   5. If perpendicular distance < cone radius at that depth → pixel is revealed.
    //   6. Takes the maximum reveal across all samples for robust coverage.
    //   7. Handles alpha for MR passthrough: dark areas block passthrough,
    //      cone area preserves transparency so passthrough shows through.
    //
    // NO DEPTH DEPENDENCY — works on Quest MR with passthrough.
    // NO CAMERA.MAIN DEPENDENCY — all data comes via global shader properties.
    //
    // CONVENTION: _Brightness 0 = complete darkness, 1 = no shader effect.
    // ──────────────────────────────────────────────────────────────────────────

    Properties
    {
        _Brightness   ("Room Brightness (0=dark, 1=bright)", Range(0, 1)) = 0.3
        _BeamRange    ("Beam Range",      Float)              = 15.0
        _EdgeSoftness ("Edge Softness",   Range(0.01, 0.15))  = 0.05
        _BeamStrength ("Beam Strength (reveal 0-1)", Range(0, 1)) = 0.75
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "DarknessOverlayPass"

            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            // XR / platform multi-compile
            #pragma multi_compile_fragment _ _LINEAR_TO_SRGB_CONVERSION
            #pragma multi_compile _ _USE_DRAW_PROCEDURAL
            #pragma multi_compile_fragment _ DEBUG_DISPLAY

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            // ── Globals set by FlashlightController.cs ────────────────────────────

            float  _Brightness;       // 0 = dark, 1 = bright
            float4 _LightWorldPos;    // Flashlight world position
            float4 _LightDir;         // Flashlight forward direction (normalized)
            float  _LightCosAngle;    // cos(halfAngle) of the cone
            float  _BeamRange;        // Max beam reach in world units
            float  _EdgeSoftness;     // Smooth transition width at cone edge
            float  _BeamStrength;     // Max reveal in the beam (1 = full, 0.75 = dimmer/moodier)
            float4 _LightOffsetFromCam; // Light position as offset from camera (ATW-safe)

            // ── Fragment ─────────────────────────────────────────────────────────

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.texcoord;
                float4 sceneColor = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);

                // Early-out: no darkness when fully bright
                if (_Brightness >= 0.999)
                    return sceneColor;

                // ── Reconstruct camera ray (world-space, no depth) ───────────────

                float2 ndc = uv * 2.0 - 1.0;

                #if UNITY_UV_STARTS_AT_TOP
                    // The scene is rendered into a flipped intermediate target
                    // (D3D/Vulkan convention), so the inverse VP matrix maps
                    // clip coords whose Y is inverted relative to the Blit UVs.
                    // Without this flip the reconstructed ray is vertically
                    // mirrored: yaw looks fine, but the reveal follows head
                    // PITCH and the vertical aim feels inverted.
                    ndc.y = -ndc.y;
                #endif

                #if UNITY_REVERSED_Z
                    float4 clipFar = float4(ndc, 0.0, 1.0);
                #else
                    float4 clipFar = float4(ndc, 1.0, 1.0);
                #endif

                float4 worldFar4 = mul(UNITY_MATRIX_I_VP, clipFar);
                float3 farPoint  = worldFar4.xyz / worldFar4.w;

                // Derive camera position from the inverse view matrix rather
                // than _WorldSpaceCameraPos. In XR, late-latching updates
                // UNITY_MATRIX_I_VP to the latest head pose at render time,
                // but _WorldSpaceCameraPos may still hold the pose from
                // script Update(). This desync makes the ray origin stale,
                // causing the cone to visually follow head movement.
                // UNITY_MATRIX_I_V is part of the same matrix chain as I_VP,
                // so its translation column gives a frame-accurate camera pos.
                float3 camPos = float3(UNITY_MATRIX_I_V[0].w,
                                       UNITY_MATRIX_I_V[1].w,
                                       UNITY_MATRIX_I_V[2].w);
                float3 rayDir = normalize(farPoint - camPos);

                // ── Multi-depth cone test ────────────────────────────────────────
                //
                // Without depth data, we can't know where real-world surfaces are.
                // We test cone membership on several planes perpendicular to the
                // beam axis, spread along the whole beam range, and take the
                // maximum reveal factor. The union of the discs approximates the
                // full world-space cone volume anchored to the flashlight.

                // Use the absolute world-space flashlight position directly.
                // This is set once per frame from the controller and never changes
                // with head movement, keeping the beam locked to the flashlight.
                float3 lightPos = _LightWorldPos.xyz;
                float3 lightDir = normalize(_LightDir.xyz);

                // Pre-compute tan(halfAngle) for cone radius
                float cosTheta = _LightCosAngle;
                float sinTheta = sqrt(max(1.0 - cosTheta * cosTheta, 0.0));
                float tanTheta = sinTheta / max(cosTheta, 0.001);

                // Edge softness: controls how many "cone radii" the edge spans
                float soft = _EdgeSoftness * 5.0;

                float bestReveal = 0.0;

                // How aligned is the camera ray with the beam axis?
                float rayDotAxis = dot(rayDir, lightDir);

                // Only do the multi-plane test if the ray isn't perpendicular to
                // the beam axis (perpendicular rays can never enter the cone).
                if (abs(rayDotAxis) > 0.001)
                {
                    // Sample axial planes distributed along the whole beam length.
                    // The planes are fixed in world space relative to the flashlight,
                    // so the union of the per-plane discs approximates the full 3D
                    // cone volume. A single plane produces a flat disc floating in
                    // mid-air, which appears to slide around with head parallax —
                    // covering the full length keeps the reveal shaped like a beam
                    // that is anchored to the flashlight only.

                    [unroll]
                    for (int i = 0; i < 8; i++)
                    {
                        // axial distance from flashlight: 8 planes from ~6% to ~94% of range
                        float d = (((float)i + 0.5) / 8.0) * _BeamRange;

                        // Intersect the camera ray with the plane perpendicular to
                        // the beam axis at distance d from the flashlight.
                        //
                        // Plane: point = lightPos + lightDir * d, normal = lightDir
                        // Ray:   camPos + t * rayDir
                        // t = dot(planePoint - camPos, lightDir) / dot(rayDir, lightDir)

                        float3 planeCenter = lightPos + lightDir * d;
                        float  t = dot(planeCenter - camPos, lightDir) / rayDotAxis;

                        // Intersection must be in front of the camera
                        if (t > 0.0)
                        {
                            float3 hitPoint = camPos + rayDir * t;

                            // Perpendicular distance from the cone axis at this plane
                            float  perpDist = length(hitPoint - planeCenter);

                            // Cone radius at axial distance d
                            float  coneRad = d * tanTheta;

                            // Ratio: 0 = on axis, 1 = at cone edge, >1 = outside
                            float  ratio = perpDist / max(coneRad, 0.001);

                            // Smooth edge: 1 inside → 0 outside
                            float  cf = 1.0 - smoothstep(1.0 - soft, 1.0 + soft, ratio);

                            // Distance falloff along the beam axis
                            float  df = 1.0 - saturate(d / max(_BeamRange, 0.001));

                            bestReveal = max(bestReveal, cf * df);
                        }
                    }
                }

                // The beam never reveals to FULL brightness — it's capped at
                // _BeamStrength, so even the beam core keeps some shadow (this
                // is the atmosphere knob). 1 = old full-reveal behaviour,
                // 0.75 = a dimmer, moodier flashlight. Scaling the reveal here
                // dims BOTH the lit look and how much passthrough the beam
                // uncovers, so the real room stays a touch darker under the beam.
                float revealFactor = saturate(bestReveal) * _BeamStrength;

                // ── Compose final output ─────────────────────────────────────────

                float darkness  = 1.0 - _Brightness;
                float dimFactor = lerp(_Brightness, 1.0, revealFactor);

                float3 finalRGB = sceneColor.rgb * dimFactor;

                // Alpha: block passthrough in dark areas, preserve in beam
                float darknessAlpha = darkness * (1.0 - revealFactor);
                float finalAlpha    = max(sceneColor.a, darknessAlpha);

                return float4(finalRGB, finalAlpha);
            }

            ENDHLSL
        }
    }

    FallBack Off
}
