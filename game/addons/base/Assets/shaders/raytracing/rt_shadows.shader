// ─────────────────────────────────────────────────────────────────────────────
// rt_shadows.shader  –  Ray-traced sun / directional shadow mask
//
// Dispatched once per frame at the resolution determined by RayTracingQuality:
//   Low    → ¼ res    (temporal upscale to full-res in the lighting pass)
//   Medium → ½ res
//   High   → full res
//
// Outputs:
//   RTShadowOutput  (RWTexture2D<float>)  –  shadow factor [0=shadow, 1=lit]
// ─────────────────────────────────────────────────────────────────────────────

HEADER
{
    Description = "Hardware ray-traced directional shadow mask";
}

//─────────────────────────────────────────────────────────────────────────────
MODES
{
    Default();
}

//─────────────────────────────────────────────────────────────────────────────
FEATURES
{
    // Quality level driven by RayTracingQuality enum: 1=Low, 2=Medium, 3=High
    DynamicCombo( D_RT_QUALITY, 1..3, Sys( PC ) );
}

//─────────────────────────────────────────────────────────────────────────────
RTX
{
    #include "system.fxc"
    #include "common.fxc"

    #include "common/classes/Depth.hlsl"
    #include "common/classes/Normals.hlsl"
    #include "common/classes/Raytracing.hlsl"

    // ── Inputs ────────────────────────────────────────────────────────────────
    // Depth chain and normals G-buffer are forwarded by RayTracingLayer.
    // (Depth.hlsl already declares g_tDepthChain; Normals.hlsl declares the index.)

    // Sun/directional light direction (world space, pointing toward the sun).
    float3 g_vSunDir < Attribute( "SunDirection" ); Default3( 0, 0, 1 ); >;

    // Maximum shadow ray length in world units.
    float g_flShadowRayLength < Attribute( "ShadowRayLength" ); Default( 8192.0f ); >;

    // ── Output ────────────────────────────────────────────────────────────────
    RWTexture2D<float> g_tShadowOutput < Attribute( "RTShadowOutput" ); >;

    // ── Closest-hit shader ───────────────────────────────────────────────────
    // We use RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER
    // so this body is intentionally empty – the miss shader sets the factor to 1.

    [shader("closesthit")]
    void ShadowClosestHit( inout ShadowPayload payload, in BuiltInTriangleIntersectionAttributes attr )
    {
        // Hit → fully shadowed (payload default is already 1.0, but we explicitly set 0 here)
        payload.ShadowFactor = 0.0f;
    }

    // ── Miss shader ───────────────────────────────────────────────────────────
    [shader("miss")]
    void ShadowMiss( inout ShadowPayload payload )
    {
        // Ray reached max distance without hitting anything → fully lit
        payload.ShadowFactor = 1.0f;
    }

    // ── Ray generation shader ─────────────────────────────────────────────────
    [shader("raygeneration")]
    void ShadowRayGen()
    {
        uint2 launchIndex = DispatchRaysIndex().xy;
        uint2 launchDim   = DispatchRaysDimensions().xy;

        // Map dispatch pixel to full viewport UV (handles sub-resolution dispatches).
        float2 uv = ( float2(launchIndex) + 0.5f ) / float2(launchDim);

        // Default: fully lit (will be overwritten only for valid geometry pixels).
        float shadow = 1.0f;

        // Reconstruct world-space position from the depth buffer.
        float2 screenPos = uv * g_vViewportSize;
        float  depth     = Depth::Get( screenPos );

        // Sky pixels (depth == 0 or 1 depending on convention) → fully lit.
        if ( depth > 0.0f && depth < 1.0f )
        {
            float3 worldPos = Depth::GetWorldPosition( (int2)screenPos );
            float3 normal   = Normals::Sample( (int2)screenPos );

            // Self-shadow bias: push origin along the surface normal to avoid acne.
            float3 origin = worldPos + normal * 0.5f;

            // Trace a shadow ray toward the sun.
            shadow = Raytracing::TraceShadowRay( origin, normalize(g_vSunDir), g_flShadowRayLength );
        }

        g_tShadowOutput[launchIndex] = shadow;
    }
}
