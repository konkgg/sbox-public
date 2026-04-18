using NativeEngine;
using Sandbox.Engine.Settings;

namespace Sandbox.Rendering;

/// <summary>
/// A render pipeline layer that dispatches hardware ray-traced shadows and reflections
/// after the depth/normal pre-pass and writes the results into dedicated render targets
/// for consumption by the lighting and specular resolve passes.
/// </summary>
/// <remarks>
/// The layer is a no-op when:
/// <list type="bullet">
///   <item>The user has set <see cref="RayTracingQuality"/> to <see cref="RayTracingQuality.Off"/>,</item>
///   <item>or the current GPU does not support hardware ray tracing (<see cref="Graphics.SupportsRayTracing"/> is <c>false</c>).</item>
/// </list>
/// In both cases the downstream lighting pass falls back to screen-space shadows / SSR automatically
/// because the shadow-mask and reflection-radiance attributes remain unset.
/// </remarks>
internal class RayTracingLayer : ProceduralRenderLayer
{
	// Output render targets written by this layer and consumed downstream.
	private RenderTarget _rtShadowMask;
	private RenderTarget _rtReflectionRadiance;

	// The TLAS for the current frame's scene geometry.
	private RayTracingAccelerationStructure _sceneTLAS;

	/// <summary>
	/// Gets or sets the Top-Level Acceleration Structure used by this layer.
	/// Game and addon code should build a TLAS each frame from all scene BLAS instances and assign
	/// it here before the frame is submitted so the RT dispatch can traverse the full scene.
	/// </summary>
	public RayTracingAccelerationStructure SceneTLAS
	{
		get => _sceneTLAS;
		set => _sceneTLAS = value;
	}

	// Ray-tracing shaders (loaded lazily the first time the layer is active).
	private RayTracingShader _shadowShader;
	private RayTracingShader _reflectionShader;

	// Viewport dimensions captured during Setup, used for dispatch sizing and scaled resolution.
	private int _viewportWidth;
	private int _viewportHeight;

	// Attribute name constants shared with the downstream lighting / specular-resolve shaders.
	private const string ShadowMaskAttributeName = "RTShadowMask";
	private const string ReflectionRadianceAttributeName = "RTReflectionRadiance";

	public RayTracingLayer()
	{
		Name = "Ray Tracing";
		Flags |= LayerFlags.NeverRemove;
	}

	/// <summary>
	/// Configures the layer for the current frame view. Call once per view before
	/// <see cref="RenderLayer.AddToView"/>.
	/// </summary>
	/// <param name="view">The scene view being rendered.</param>
	/// <param name="viewport">The viewport rectangle for the current view.</param>
	/// <param name="tlas">
	/// The Top-Level Acceleration Structure for this frame. When <c>null</c> the layer will
	/// use whatever TLAS was last assigned to <see cref="SceneTLAS"/>. If both are null the
	/// dispatch is skipped.
	/// </param>
	public void Setup( ISceneView view, RenderViewport viewport, RayTracingAccelerationStructure tlas = null )
	{
		if ( tlas != null )
			_sceneTLAS = tlas;

		_viewportWidth = (int)viewport.Rect.Width;
		_viewportHeight = (int)viewport.Rect.Height;

		var quality = RenderSettings.Instance.RayTracingQuality;

		// Compute the dispatch resolution based on quality level.
		// Lower levels use fractional resolution and rely on temporal upscaling.
		(int w, int h) = quality switch
		{
			RayTracingQuality.Low => (_viewportWidth / 4, _viewportHeight / 4),
			RayTracingQuality.Medium => (_viewportWidth / 2, _viewportHeight / 2),
			_ => (_viewportWidth, _viewportHeight),   // High
		};

		// Shadow mask: R16 single-channel is sufficient for a binary/soft shadow factor.
		_rtShadowMask = RenderTarget.GetTemporary( w, h,
			colorFormat: ImageFormat.R16,
			depthFormat: ImageFormat.None );

		// Reflection radiance: HDR colour.
		_rtReflectionRadiance = RenderTarget.GetTemporary( w, h,
			colorFormat: ImageFormat.RGBA16161616F,
			depthFormat: ImageFormat.None );
	}

	internal override void OnRender()
	{
		// Guard: disabled by quality setting or unsupported hardware.
		var quality = RenderSettings.Instance.RayTracingQuality;
		if ( quality == RayTracingQuality.Off || !Graphics.SupportsRayTracing )
			return;

		if ( _sceneTLAS == null || !_sceneTLAS.IsValid() )
			return;

		// Lazy-load shaders.
		_shadowShader ??= new RayTracingShader( "shaders/raytracing/rt_shadows.shader" );
		_reflectionShader ??= new RayTracingShader( "shaders/raytracing/rt_reflections.shader" );

		var dispatchW = _rtShadowMask != null ? _rtShadowMask.Width : _viewportWidth;
		var dispatchH = _rtShadowMask != null ? _rtShadowMask.Height : _viewportHeight;

		// ── Ray-traced shadows ────────────────────────────────────────────────────
		{
			_shadowShader.BindAccelerationStructure( _sceneTLAS );

			// Output UAV: the shadow mask texture.
			_shadowShader.Attributes.Set( "RTShadowOutput", _rtShadowMask?.ColorTarget );

			// Forward the G-buffer normals and depth chain so the ray-gen shader can reconstruct
			// world-space positions and orient shadow rays toward the sun.
			_shadowShader.Attributes.Set( "DepthChainDownsample",
				Graphics.FrameAttributes.GetTexture( "DepthChainDownsample" ) );
			_shadowShader.Attributes.Set( "NormalsTextureIndex",
				Graphics.FrameAttributes.GetInt( "NormalsTextureIndex" ) );

			// Quality combo drives sample count in the HLSL.
			_shadowShader.Attributes.SetCombo( "D_RT_QUALITY", (int)quality );

			_shadowShader.DispatchRays( dispatchW, dispatchH, 1 );

			// Expose the shadow mask texture as a frame attribute so the lighting pass can
			// blend it with (or replace) the conventional shadow-map lookup.
			Graphics.FrameAttributes.Set( ShadowMaskAttributeName, _rtShadowMask?.ColorTarget );
		}

		// ── Ray-traced reflections ────────────────────────────────────────────────
		{
			_reflectionShader.BindAccelerationStructure( _sceneTLAS );

			// Output UAV: HDR reflection radiance.
			_reflectionShader.Attributes.Set( "RTReflectionOutput", _rtReflectionRadiance?.ColorTarget );

			_reflectionShader.Attributes.Set( "DepthChainDownsample",
				Graphics.FrameAttributes.GetTexture( "DepthChainDownsample" ) );
			_reflectionShader.Attributes.Set( "NormalsTextureIndex",
				Graphics.FrameAttributes.GetInt( "NormalsTextureIndex" ) );

			_reflectionShader.Attributes.SetCombo( "D_RT_QUALITY", (int)quality );

			_reflectionShader.DispatchRays( dispatchW, dispatchH, 1 );

			// Expose reflection radiance so DynamicReflections.hlsl can sample it.
			// It reads "ReflectionColorIndex" which is filled by the attribute system when we
			// set "RTReflectionRadiance" – the shader side maps that name to its slot.
			Graphics.FrameAttributes.Set( ReflectionRadianceAttributeName, _rtReflectionRadiance?.ColorTarget );
			Graphics.FrameAttributes.Set( "ReflectionColorIndex", _rtReflectionRadiance?.ColorTarget );
		}
	}
}
