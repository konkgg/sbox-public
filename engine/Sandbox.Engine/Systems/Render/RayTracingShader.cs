using NativeEngine;

namespace Sandbox;

/// <summary>
/// A ray tracing shader, enabling advanced rendering techniques like real-time ray tracing
/// for reflections, global illumination, and shadows using DXR / Vulkan KHR ray tracing.
/// </summary>
/// <remarks>
/// Use <see cref="BindAccelerationStructure"/> to provide the scene TLAS before dispatching,
/// and check <see cref="Graphics.SupportsRayTracing"/> to guard against unsupported hardware.
/// </remarks>
/// <seealso cref="GpuBuffer{T}"/>
/// <seealso cref="ComputeShader"/>
/// <seealso cref="RayTracingAccelerationStructure"/>
public class RayTracingShader
{
	/// <summary>
	/// Attributes that are passed to the ray tracing shader on dispatch.
	/// </summary>
	public RenderAttributes Attributes { get; } = new RenderAttributes();

	private Material RayTracingMaterial;

	/// <summary>
	/// Create a ray tracing shader from the specified shader path.
	/// </summary>
	/// <param name="path">The path to the shader file (must use the RTX program stage).</param>
	public RayTracingShader( string path )
	{
		var material = Material.FromShader( path );
		Assert.NotNull( material, $"Failed to load ray tracing shader material from path: {path}" );
		RayTracingMaterial = material;
	}

	/// <summary>
	/// Binds a Top-Level Acceleration Structure (TLAS) to the shader so that ray intersection
	/// tests can traverse the full scene geometry.
	/// </summary>
	/// <remarks>
	/// The TLAS is bound under the well-known attribute name <c>"SceneAccelerationStructure"</c>
	/// which maps to the <c>_accelStruct</c> binding in <c>common/classes/Raytracing.hlsl</c>.
	/// </remarks>
	/// <param name="tlas">
	/// The top-level acceleration structure to bind. Must be valid (built this frame).
	/// </param>
	public void BindAccelerationStructure( RayTracingAccelerationStructure tlas )
	{
		ArgumentNullException.ThrowIfNull( tlas );

		if ( !tlas.IsValid() )
			throw new ArgumentException( "The acceleration structure is not valid. Ensure it has been successfully built.", nameof( tlas ) );

		// The TLAS native handle is stored as a pointer attribute; the shader compiler maps the
		// "SceneAccelerationStructure" attribute name to the ExternalDescriptorSet binding in
		// common/classes/Raytracing.hlsl.
		Attributes.SetPointer( "SceneAccelerationStructure", RenderTools.GetAccelerationStructureHandle( tlas.native ) );
	}

	/// <summary>
	/// Dispatches the ray tracing shader using explicit thread counts.
	/// </summary>
	/// <remarks>
	/// The specified thread counts represent the dispatch dimensions for the ray generation shader.
	/// <para>
	/// When called outside a graphics context, the dispatch runs immediately.  
	/// When called inside a graphics context, the dispatch runs async.
	/// </para>
	/// </remarks>
	/// <param name="threadsX">The number of threads to dispatch in the X dimension.</param>
	/// <param name="threadsY">The number of threads to dispatch in the Y dimension.</param>
	/// <param name="threadsZ">The number of threads to dispatch in the Z dimension.</param>
	public void DispatchRays( int threadsX = 1, int threadsY = 1, int threadsZ = 1 )
	{
		DispatchRaysWithAttributes( Attributes, threadsX, threadsY, threadsZ );
	}

	/// <summary>
	/// Dispatches the ray tracing shader using explicit thread counts and the provided attributes.
	/// </summary>
	/// <param name="attributes">Render attributes to use for this dispatch.</param>
	/// <param name="threadsX">The number of threads to dispatch in the X dimension.</param>
	/// <param name="threadsY">The number of threads to dispatch in the Y dimension.</param>
	/// <param name="threadsZ">The number of threads to dispatch in the Z dimension.</param>
	public void DispatchRaysWithAttributes( RenderAttributes attributes, int threadsX = 1, int threadsY = 1, int threadsZ = 1 )
	{
		if ( threadsX < 1 ) throw new ArgumentException( $"Cannot be less than 1", nameof( threadsX ) );
		if ( threadsY < 1 ) throw new ArgumentException( $"Cannot be less than 1", nameof( threadsY ) );
		if ( threadsZ < 1 ) throw new ArgumentException( $"Cannot be less than 1", nameof( threadsZ ) );

		var mode = RayTracingMaterial.native.GetMode();
		RenderTools.TraceRays( Graphics.Context, attributes.Get(), mode, (uint)threadsX, (uint)threadsY, (uint)threadsZ );
	}

	/// <summary>
	/// Dispatches the ray tracing shader by reading dispatch arguments from an indirect buffer.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <paramref name="indirectBuffer"/> must be created with <see cref="GpuBuffer.UsageFlags.IndirectDrawArguments"/>  
	/// and have an element size of 12 bytes (3 uint32 values for X, Y, Z dimensions).
	/// </para>
	/// <para>
	/// <paramref name="indirectElementOffset"/> is an element index into <paramref name="indirectBuffer"/>, not a byte offset.
	/// </para>
	/// </remarks>
	/// <param name="indirectBuffer">The GPU buffer containing one or more dispatch argument entries.</param>
	/// <param name="indirectElementOffset">The index of the dispatch arguments element to use (each element = 12 bytes).</param>
	public void DispatchRaysIndirect( GpuBuffer indirectBuffer, uint indirectElementOffset = 0 )
	{
		DispatchRaysIndirectWithAttributes( Attributes, indirectBuffer, indirectElementOffset );
	}

	/// <inheritdoc cref="DispatchRaysIndirect"/>
	public void DispatchRaysIndirectWithAttributes( RenderAttributes attributes, GpuBuffer indirectBuffer, uint indirectElementOffset = 0 )
	{
		if ( !indirectBuffer.IsValid() )
			throw new ArgumentException( $"Invalid buffer", nameof( indirectBuffer ) );

		if ( indirectBuffer.ElementSize != 12 )
			throw new ArgumentException( $"Buffer element size must be 12 bytes", nameof( indirectBuffer ) );

		if ( indirectElementOffset >= indirectBuffer.ElementCount )
			throw new ArgumentOutOfRangeException( nameof( indirectElementOffset ), "Indirect element offset exceeds buffer bounds" );

		if ( !indirectBuffer.Usage.Contains( GpuBuffer.UsageFlags.IndirectDrawArguments ) )
			throw new ArgumentException( $"Buffer must have the required usage flag '{GpuBuffer.UsageFlags.IndirectDrawArguments}'", nameof( indirectBuffer ) );

		var mode = RayTracingMaterial.native.GetMode();
		RenderTools.TraceRaysIndirect( Graphics.Context, attributes.Get(), mode, indirectBuffer.native, indirectElementOffset * 12 );
	}
}
