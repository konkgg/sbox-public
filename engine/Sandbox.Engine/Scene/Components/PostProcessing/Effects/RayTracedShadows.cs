using NativeEngine;
using Sandbox.Rendering;

namespace Sandbox;

/// <summary>
/// Adds ray traced contact shadows using hardware DXR / Vulkan RT acceleration.
/// Rays are cast from each visible surface pixel toward the primary directional light.
/// Surfaces that are occluded before reaching the light receive a darkening multiplier.
/// </summary>
/// <remarks>
/// This effect requires a GPU that supports hardware ray tracing (DXR tier 1.0+ or
/// VK_KHR_ray_tracing_pipeline).  When hardware support is absent the component
/// silently does nothing.
/// Enable or disable at runtime via the <c>r_rt_shadows</c> console variable.
/// </remarks>
[Expose]
[Title( "Ray Traced Shadows" )]
[Category( "Post Processing" )]
[Icon( "contrast" )]
public sealed class RayTracedShadows : BasePostProcess<RayTracedShadows>
{
	/// <summary>
	/// Master on/off switch.  Disabled by default because ray tracing requires
	/// capable hardware and has a non-trivial GPU cost.
	/// </summary>
	[ConVar( "r_rt_shadows", Help = "Enable ray traced contact shadows (requires DXR / Vulkan RT hardware)." )]
	internal static bool EnableRTShadows { get; set; } = false;

	/// <summary>
	/// Maximum distance in world units that each shadow ray travels.
	/// Shorter distances are faster but miss distant occluders.
	/// </summary>
	[Property, Range( 10, 2000 ), Category( "Properties" )]
	public float ShadowDistance { get; set; } = 500.0f;

	/// <summary>
	/// Blending weight of the shadow mask over the final scene colour.
	/// 0 = no shadowing, 1 = full shadowing.
	/// </summary>
	[Property, Range( 0, 1 ), Category( "Properties" )]
	public float Intensity { get; set; } = 1.0f;

	/// <summary>
	/// World-space direction pointing from surfaces toward the light source.
	/// Defaults to straight up, which matches a noon-sun position.
	/// Override this to match your scene's primary directional light.
	/// </summary>
	[Property, Category( "Properties" )]
	public Vector3 LightDirection { get; set; } = Vector3.Up;

	/// <summary>
	/// World-space bias applied at each ray origin along the surface normal to
	/// avoid self-intersection artefacts on flat surfaces.
	/// </summary>
	[Property, Range( 0, 10 ), Category( "Advanced" )]
	public float NormalBias { get; set; } = 0.5f;

	// ---------------------------------------------------------------------------
	// Static shader / hardware-check state (shared across all component instances)
	// ---------------------------------------------------------------------------

	static RayTracingShader _rtShader;
	static Material _compositeMaterial;

	static bool _hwChecked;
	static bool _hwSupported;

	/// <summary>
	/// Returns true when the current GPU supports hardware ray tracing.
	/// The result is cached after the first call.
	/// </summary>
	static bool IsHardwareSupported()
	{
		if ( _hwChecked )
			return _hwSupported;

		_hwChecked = true;

		// On Vulkan, query for the RT pipeline extension properties.
		// A non-default (non-zero) pointer means the feature is present.
		var rtProps = g_pRenderDevice.GetDeviceSpecificInfo(
			DeviceSpecificInfo_t.DSI_VULKAN_RAY_TRACING_PIPELINE_PROPERTIES );

		if ( rtProps != default )
		{
			_hwSupported = true;
			return true;
		}

		// On non-Vulkan backends (e.g. DX12/DXR), attempt to load the RT shader.
		// A successful load indicates the runtime supports ray tracing dispatch.
		try
		{
			_rtShader    = new RayTracingShader( "shaders/raytracing/rt_shadows" );
			_hwSupported = _rtShader is not null;
		}
		catch
		{
			_hwSupported = false;
		}

		if ( !_hwSupported )
			Log.Info( "RayTracedShadows: hardware ray tracing is not supported on this device." );

		return _hwSupported;
	}

	static void EnsureShaders()
	{
		_rtShader         ??= new RayTracingShader( "shaders/raytracing/rt_shadows" );
		_compositeMaterial ??= Material.FromShader( "shaders/postprocess/pp_rt_shadows" );
	}

	// ---------------------------------------------------------------------------
	// Per-instance command lists (reset each frame during Render)
	// ---------------------------------------------------------------------------

	// Runs at AfterDepthPrepass: dispatches RT rays and exposes the occlusion
	// mask as a global (frame-scope) attribute for the composite pass.
	readonly CommandList _rtCmd = new CommandList( "RT Shadows Dispatch" );

	// Runs at AfterOpaque: grabs the frame colour buffer and blends in the mask.
	readonly CommandList _compositeCmd = new CommandList( "RT Shadows Composite" );

	// ---------------------------------------------------------------------------
	// BasePostProcess entry point
	// ---------------------------------------------------------------------------

	/// <inheritdoc />
	public override void Render()
	{
		if ( !EnableRTShadows )
			return;

		if ( Application.IsDedicatedServer )
			return;

		if ( !IsHardwareSupported() )
			return;

		EnsureShaders();

		// -----------------------------------------------------------------------
		// RT dispatch pass  –  inserted at AfterDepthPrepass so the depth buffer
		// is fully populated but the opaque colour pass has not yet run.
		// -----------------------------------------------------------------------
		_rtCmd.Reset();

		// Allocate a half-precision single-channel RT for the shadow mask.
		var shadowOutput = _rtCmd.GetRenderTarget( "RTShadowOutput", ImageFormat.R16F );

		// Shader inputs
		_rtCmd.Attributes.Set( "ShadowOutput",      shadowOutput.ColorTexture );
		_rtCmd.Attributes.Set( "LightDirection",    GetWeighted( x => x.LightDirection, Vector3.Up ).Normal );
		_rtCmd.Attributes.Set( "ShadowDistance",    GetWeighted( x => x.ShadowDistance, 500.0f ) );
		_rtCmd.Attributes.Set( "NormalBias",        GetWeighted( x => x.NormalBias, 0.5f ) );

		// Dispatch one thread per screen pixel.
		_rtCmd.DispatchRays( _rtShader, _rtCmd.ViewportSize );

		// Transition the output to a readable state before the composite reads it.
		_rtCmd.ResourceBarrierTransition( shadowOutput, ResourceState.PixelShaderResource );

		// Expose the texture index globally so the composite shader can sample it.
		_rtCmd.GlobalAttributes.Set( "RTShadowsIndex", shadowOutput.ColorIndex );

		InsertCommandList( _rtCmd, Rendering.Stage.AfterDepthPrepass, int.MaxValue, "RT Shadows Dispatch" );

		// -----------------------------------------------------------------------
		// Composite pass  –  inserted at AfterOpaque so the scene has been
		// rendered and the colour buffer contains full-scene data to darken.
		// -----------------------------------------------------------------------
		_compositeCmd.Reset();

		// Copy the current colour buffer so the blit shader can read it.
		_compositeCmd.Attributes.GrabFrameTexture( "ColorBuffer" );

		// RTShadowsIndex was written to the frame attributes by _rtCmd above and
		// is therefore visible here via the frame-attribute hierarchy.
		_compositeCmd.Attributes.Set( "Intensity", GetWeighted( x => x.Intensity, 1.0f ) );

		_compositeCmd.Blit( _compositeMaterial );

		InsertCommandList( _compositeCmd, Rendering.Stage.AfterOpaque, 0, "RT Shadows Composite" );
	}
}
