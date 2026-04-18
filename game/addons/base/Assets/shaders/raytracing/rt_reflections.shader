// ─────────────────────────────────────────────────────────────────────────────
// rt_reflections.shader  –  Ray-traced specular reflections
//
// Dispatched once per frame at a resolution determined by RayTracingQuality.
// Reads the world-space normals from the G-buffer to compute reflection directions,
// then traces rays into the scene and accumulates radiance.
//
// Outputs:
//   RTReflectionOutput  (RWTexture2D<float4>)  –  HDR reflected colour (RGB) + hit distance (A)
// ─────────────────────────────────────────────────────────────────────────────

HEADER
{
    Description = "Hardware ray-traced screen-space reflections";
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
    // Sky / environment colour used as a fallback when a reflection ray misses all geometry.
    // In practice this is sampled from the cubemap / atmosphere shader, but a simple
    // ambient tint is sufficient here until a sky-sampling SRV is plumbed through.
    float3 g_vSkyColour < Attribute( "SkyColour" ); Default3( 0.2f, 0.4f, 0.8f ); >;

    // Per-pixel roughness threshold: pixels rougher than this are considered diffuse and
    // not worth ray tracing (SSR handles them).  Matches the GBuffer roughness encoding.
    float g_flMaxRoughness < Attribute( "RTMaxRoughness" ); Default( 0.5f ); >;

    // Maximum reflection ray length (world units).
    float g_flReflectionRayLength < Attribute( "ReflectionRayLength" ); Default( 8192.0f ); >;

    // ── Output ────────────────────────────────────────────────────────────────
    RWTexture2D<float4> g_tReflectionOutput < Attribute( "RTReflectionOutput" ); >;

    // ── Closest-hit shader ────────────────────────────────────────────────────
    [shader("closesthit")]
    void ReflectionClosestHit( inout ReflectionPayload payload, in BuiltInTriangleIntersectionAttributes attr )
    {
        payload.HitT = RayTCurrent();

        // A production implementation would evaluate the hit surface's BRDF here.
        // For now we use a simple ambient + emissive colour reconstructed from the instance index
        // as a placeholder that exercises the full RT pipeline path.
        uint instanceID = InstanceID();
        float3 albedo = float3(
            frac( instanceID * 0.3731f ),
            frac( instanceID * 0.7119f ),
            frac( instanceID * 0.1547f )
        );

        payload.Radiance = albedo;
    }

    // ── Miss shader ───────────────────────────────────────────────────────────
    [shader("miss")]
    void ReflectionMiss( inout ReflectionPayload payload )
    {
        // Ray escaped the scene → use the sky colour as the reflection.
        payload.Radiance = g_vSkyColour;
        payload.HitT     = -1.0f;
    }

    // ── Ray generation shader ─────────────────────────────────────────────────
    [shader("raygeneration")]
    void ReflectionRayGen()
    {
        uint2 launchIndex = DispatchRaysIndex().xy;
        uint2 launchDim   = DispatchRaysDimensions().xy;

        // Map dispatch pixel to full viewport UV.
        float2 uv         = ( float2(launchIndex) + 0.5f ) / float2(launchDim);
        float2 screenPos  = uv * g_vViewportSize;

        // Default: transparent / black (no reflection).
        float4 result = float4( 0, 0, 0, -1 );

        float depth = Depth::Get( screenPos );
        if ( depth > 0.0f && depth < 1.0f )
        {
            float3 worldPos  = Depth::GetWorldPosition( (int2)screenPos );
            float3 normal    = Normals::Sample( (int2)screenPos );

            // Read roughness from the G-buffer (stored in the alpha channel of NormalsTexture).
            // Skip reflection for rough / diffuse surfaces.
            float roughness = Roughness::Sample( (int2)screenPos );
            if ( roughness <= g_flMaxRoughness )
            {
                // Compute view vector and perfect mirror reflection direction.
                float3 viewDir   = normalize( worldPos - g_vCameraPositionWs );
                float3 reflDir   = reflect( viewDir, normal );

                // Bias origin along normal to avoid self-intersection.
                float3 origin = worldPos + normal * 0.5f;

                ReflectionPayload payload = Raytracing::TraceReflectionRay( origin, reflDir, g_flReflectionRayLength );

                result = float4( payload.Radiance, payload.HitT );
            }
        }

        g_tReflectionOutput[launchIndex] = result;
    }
}
