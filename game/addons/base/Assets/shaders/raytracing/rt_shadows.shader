//-------------------------------------------------------------------------------------------------------------------------------------------------------------
HEADER
{
	DevShader = true;
	Description = "Ray traced contact shadows - dispatches shadow rays against the scene TLAS";
}

//-------------------------------------------------------------------------------------------------------------------------------------------------------------
MODES
{
	Default();
}

//-------------------------------------------------------------------------------------------------------------------------------------------------------------
FEATURES
{
}

//-------------------------------------------------------------------------------------------------------------------------------------------------------------
COMMON
{
	#include "system.fxc"
	#include "common.fxc"
	#include "common_samplers.fxc"
	#include "common/classes/Raytracing.hlsl"
	#include "common/classes/Depth.hlsl"
	#include "common/classes/Normals.hlsl"

	//
	// Shadow ray payload - 0 means occluded, 1 means unoccluded.
	//
	struct ShadowPayload
	{
		float shadow;
	};

	// Output occlusion mask (R channel: 0 = shadowed, 1 = lit).
	RWTexture2D<float> g_tShadowOutput < Attribute( "ShadowOutput" ); >;

	// Direction toward the primary light source (world space, normalised).
	float3 g_vLightDirection < Attribute( "LightDirection" ); Default3( 0.0f, 0.0f, 1.0f ); >;

	// Maximum distance (world units) the shadow ray is allowed to travel.
	float g_flShadowDistance < Attribute( "ShadowDistance" ); Default( 500.0f ); >;

	// Normal bias applied at ray origin to avoid self-intersection artefacts.
	float g_flNormalBias < Attribute( "NormalBias" ); Default( 0.5f ); >;
}

//-------------------------------------------------------------------------------------------------------------------------------------------------------------
RT
{
	// -----------------------------------------------------------------------------------------
	// Ray generation – one thread per screen pixel.
	// -----------------------------------------------------------------------------------------
	[shader( "raygeneration" )]
	void RayGenMain()
	{
		uint2 launchIndex = DispatchRaysIndex().xy;
		uint2 launchDims  = DispatchRaysDimensions().xy;

		float2 screenPos = (float2)launchIndex;

		// Sample depth; pixels at the far plane (sky) receive no shadow.
		float rawDepth = Depth::GetNormalized( screenPos );
		if ( rawDepth >= 1.0f )
		{
			g_tShadowOutput[launchIndex] = 1.0f;
			return;
		}

		// Reconstruct world-space position and normal from the depth/GBuffer.
		float3 worldPos = Depth::GetWorldPosition( screenPos );
		float3 normal   = Normals::Sample( (int2)launchIndex );

		// Offset origin along the surface normal to avoid self-intersection.
		float3 origin = worldPos + normal * g_flNormalBias;

		// The shadow ray travels toward the light.
		RayDesc ray;
		ray.Origin    = origin;
		ray.Direction = normalize( g_vLightDirection );
		ray.TMin      = 0.01f;
		ray.TMax      = g_flShadowDistance;

		ShadowPayload payload;
		payload.shadow = 1.0f; // optimistic: assume lit

		TraceRay(
			Raytracing::GetAccelerationStructure(),
			// Accept the first hit and skip the closest-hit shader entirely
			// for maximum performance on opaque shadow rays.
			RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER,
			0xFF,  // instance mask – test all instances
			0,     // hit group offset
			1,     // geometry stride
			0,     // miss shader index
			ray,
			payload
		);

		g_tShadowOutput[launchIndex] = payload.shadow;
	}

	// -----------------------------------------------------------------------------------------
	// Miss shader – ray reached maximum distance without hitting anything: no shadow.
	// -----------------------------------------------------------------------------------------
	[shader( "miss" )]
	void MissMain( inout ShadowPayload payload )
	{
		payload.shadow = 1.0f;
	}

	// -----------------------------------------------------------------------------------------
	// Closest-hit shader – a surface was hit before the light: pixel is in shadow.
	// (Only reached when RAY_FLAG_SKIP_CLOSEST_HIT_SHADER is not used.)
	// -----------------------------------------------------------------------------------------
	[shader( "closesthit" )]
	void ClosestHitMain( inout ShadowPayload payload, in BuiltInTriangleIntersectionAttributes attr )
	{
		payload.shadow = 0.0f;
	}
}
