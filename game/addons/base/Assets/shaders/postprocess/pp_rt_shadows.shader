HEADER
{
	DevShader = true;
	Description = "Composite ray traced shadow mask over the rendered scene.";
}

MODES
{
	Default();
	Forward();
}

COMMON
{
	#include "postprocess/shared.hlsl"
}

struct VertexInput
{
	float3 vPositionOs : POSITION < Semantic( PosXyz ); >;
	float2 vTexCoord   : TEXCOORD0 < Semantic( LowPrecisionUv ); >;
};

struct PixelInput
{
	float2 vTexCoord : TEXCOORD0;

	// VS only
	#if ( PROGRAM == VFX_PROGRAM_VS )
		float4 vPositionPs : SV_Position;
	#endif

	// PS only
	#if ( PROGRAM == VFX_PROGRAM_PS )
		float4 vPositionSs : SV_Position;
	#endif
};

VS
{
	PixelInput MainVs( VertexInput i )
	{
		PixelInput o;
		o.vPositionPs = float4( i.vPositionOs.xy, 0.0f, 1.0f );
		o.vTexCoord   = i.vTexCoord;
		return o;
	}
}

PS
{
	#include "postprocess/common.hlsl"
	#include "common/classes/Bindless.hlsl"

	// Scene colour captured before this pass runs.
	Texture2D g_tColorBuffer < Attribute( "ColorBuffer" ); SrgbRead( true ); >;

	// Index into the bindless heap for the RT shadow occlusion mask.
	// Set to -1 when the RT pass did not run (hardware not supported, effect disabled).
	int RTShadowsIndex < Attribute( "RTShadowsIndex" ); Default( -1 ); >;

	// Blend weight: 0 = no shadow applied, 1 = full shadow applied.
	float flIntensity < Attribute( "Intensity" ); Default( 1.0f ); >;

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 vScreenUv = CalculateViewportUv( i.vPositionSs.xy );

		float4 sceneColor = g_tColorBuffer.SampleLevel( g_sBilinearClamp, vScreenUv, 0 );

		if ( RTShadowsIndex >= 0 )
		{
			// shadow in [0,1]: 0 = fully occluded, 1 = fully lit.
			float shadow       = Bindless::GetTexture2D( RTShadowsIndex ).SampleLevel( g_sBilinearClamp, vScreenUv, 0 ).r;
			float shadowFactor = lerp( 1.0f, shadow, flIntensity );
			sceneColor.rgb    *= shadowFactor;
		}

		return sceneColor;
	}
}
