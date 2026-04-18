#ifndef RAYTRACING_HLSL
#define RAYTRACING_HLSL

ExternalDescriptorSet RaytracingDescriptorSet Slot 0;
RaytracingAccelerationStructure _accelStruct EXTERNAL_DESC_SET(t, RaytracingDescriptorSet, 0);

// ─────────────────────────────────────────────────────────────────────────────
// Shared payload structures
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Payload written by the any-hit / miss / closest-hit shaders and read back
/// in the ray-generation shader after TraceRay() returns.
/// Keep this as small as possible – every byte is copied to/from all shader stages.
/// </summary>
struct ShadowPayload
{
    float ShadowFactor;     // 1.0 = fully lit, 0.0 = fully shadowed
};

/// <summary>
/// Payload for reflection rays. Carries the accumulated radiance and a hit flag.
/// </summary>
struct ReflectionPayload
{
    float3 Radiance;        // HDR reflected colour
    float  HitT;            // Ray parameter at closest hit; negative means miss
};

// ─────────────────────────────────────────────────────────────────────────────
// Raytracing utility class
// ─────────────────────────────────────────────────────────────────────────────

class Raytracing
{
    /// Returns the scene top-level acceleration structure.
    static RaytracingAccelerationStructure GetAccelerationStructure() { return _accelStruct; }

    // ─── Shadow ray helper ────────────────────────────────────────────────────

    /// <summary>
    /// Fires a shadow ray from <paramref name="origin"/> toward <paramref name="lightDir"/>
    /// up to <paramref name="maxDistance"/>. Returns 0 if occluded, 1 if unoccluded.
    /// </summary>
    static float TraceShadowRay( float3 origin, float3 lightDir, float maxDistance )
    {
        RayDesc ray;
        ray.Origin    = origin;
        ray.Direction = lightDir;
        ray.TMin      = 0.001f;   // Small bias to avoid self-intersection
        ray.TMax      = maxDistance;

        ShadowPayload payload;
        payload.ShadowFactor = 1.0f;

        TraceRay(
            _accelStruct,
            RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER,
            0xFF,       // InstanceInclusionMask – test all instances
            0,          // RayContributionToHitGroupIndex
            0,          // MultiplierForGeometryContributionToHitGroupIndex
            0,          // MissShaderIndex
            ray,
            payload
        );

        return payload.ShadowFactor;
    }

    // ─── Reflection ray helper ────────────────────────────────────────────────

    /// <summary>
    /// Fires a reflection ray from <paramref name="origin"/> in direction <paramref name="reflectDir"/>
    /// and returns the accumulated radiance. Returns the sky colour on a miss.
    /// </summary>
    static ReflectionPayload TraceReflectionRay( float3 origin, float3 reflectDir, float maxDistance = 1e27f )
    {
        RayDesc ray;
        ray.Origin    = origin;
        ray.Direction = reflectDir;
        ray.TMin      = 0.001f;
        ray.TMax      = maxDistance;

        ReflectionPayload payload;
        payload.Radiance = 0;
        payload.HitT     = -1.0f;

        TraceRay(
            _accelStruct,
            RAY_FLAG_NONE,
            0xFF,       // InstanceInclusionMask
            1,          // RayContributionToHitGroupIndex (1 = reflection hit group)
            0,          // MultiplierForGeometryContributionToHitGroupIndex
            1,          // MissShaderIndex (1 = reflection miss)
            ray,
            payload
        );

        return payload;
    }

    // ─── Legacy result struct (kept for backwards compat) ─────────────────────

    struct Result
    {
        bool Hit;
        float3 HitPosition;
        int HitInstanceID;
    };
};

#endif // RAYTRACING_HLSL