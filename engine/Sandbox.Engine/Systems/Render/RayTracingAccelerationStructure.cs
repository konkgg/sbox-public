using NativeEngine;

namespace Sandbox;

/// <summary>
/// Represents a ray tracing acceleration structure (BLAS or TLAS) that contains geometry
/// for efficient ray intersection testing via DXR / Vulkan KHR ray tracing.
/// </summary>
/// <remarks>
/// <para>
/// A Bottom-Level Acceleration Structure (BLAS) holds raw triangle geometry for a single mesh
/// or model. A Top-Level Acceleration Structure (TLAS) aggregates multiple BLAS instances
/// with per-instance world transforms and is the resource bound to the shader at dispatch time.
/// </para>
/// <para>
/// Create BLASes with <see cref="CreateFromMesh"/> or <see cref="CreateFromModel"/>, then build
/// a TLAS each frame with <see cref="BuildTLAS"/> and bind it via
/// <see cref="RayTracingShader.BindAccelerationStructure"/>.
/// </para>
/// </remarks>
public sealed class RayTracingAccelerationStructure : IDisposable
{
	internal object native;

	/// <summary>Whether the acceleration structure has been successfully built and is ready for ray tracing.</summary>
	public bool IsBuilt { get; private set; }

	/// <summary>
	/// When true the acceleration structure was created from dynamic (animated / skinned) geometry
	/// and should be updated every frame via <see cref="Update"/>.
	/// </summary>
	public bool IsDynamic { get; private set; }

	/// <summary>Returns true when the native handle is valid and the structure has been built.</summary>
	public bool IsValid() => native != null && IsBuilt;

	private RayTracingAccelerationStructure( object nativeAccelerationStructure, bool isDynamic = false )
	{
		native = nativeAccelerationStructure;
		IsBuilt = nativeAccelerationStructure != null;
		IsDynamic = isDynamic;
	}

	// ──────────────────────────────────────────────────────────────────────────────
	// BLAS builders
	// ──────────────────────────────────────────────────────────────────────────────

	/// <summary>
	/// Builds a Bottom-Level Acceleration Structure (BLAS) from a <see cref="Mesh"/>.
	/// </summary>
	/// <param name="mesh">The mesh whose triangle geometry is used to build the BLAS.</param>
	/// <param name="isDynamic">
	/// Set to <c>true</c> for skinned or otherwise deformable geometry so the engine can
	/// allocate a refittable BLAS instead of a compacted one.
	/// </param>
	/// <returns>A new BLAS, or <c>null</c> if creation failed or ray tracing is unsupported.</returns>
	public static RayTracingAccelerationStructure CreateFromMesh( Mesh mesh, bool isDynamic = false )
	{
		if ( !Graphics.SupportsRayTracing )
			return null;

		if ( mesh == null || !mesh.IsValid )
			return null;

		// Build the BLAS by passing the mesh's native render-mesh handle to the engine.
		// The native side is responsible for extracting vertex / index buffers and issuing
		// the DXR BuildRaytracingAccelerationStructure / vkCmdBuildAccelerationStructuresKHR call.
		var nativeAS = RenderTools.BuildBLAS( mesh.native, isDynamic );
		if ( nativeAS == null )
			return null;

		return new RayTracingAccelerationStructure( nativeAS, isDynamic );
	}

	/// <summary>
	/// Builds a Bottom-Level Acceleration Structure (BLAS) from all triangle meshes in a
	/// <see cref="SceneModel"/>. Skinned models should pass <paramref name="isDynamic"/> = <c>true</c>.
	/// </summary>
	/// <param name="model">The scene model to build the BLAS for.</param>
	/// <param name="isDynamic">
	/// Set to <c>true</c> for skinned or otherwise deformable geometry.
	/// </param>
	/// <returns>A new BLAS, or <c>null</c> if creation failed or ray tracing is unsupported.</returns>
	public static RayTracingAccelerationStructure CreateFromModel( SceneModel model, bool isDynamic = false )
	{
		if ( !Graphics.SupportsRayTracing )
			return null;

		if ( model == null || !model.IsValid )
			return null;

		// The native side iterates the model's LOD-0 meshes, merges them into a single BLAS,
		// and keeps a reference to the skinned-mesh scratch buffer when isDynamic is true.
		var nativeAS = RenderTools.BuildBLASFromSceneObject( model.native, isDynamic );
		if ( nativeAS == null )
			return null;

		return new RayTracingAccelerationStructure( nativeAS, isDynamic );
	}

	// ──────────────────────────────────────────────────────────────────────────────
	// TLAS builder
	// ──────────────────────────────────────────────────────────────────────────────

	/// <summary>
	/// Builds a Top-Level Acceleration Structure (TLAS) from a collection of BLAS instances.
	/// </summary>
	/// <remarks>
	/// Call this once per frame (after updating any dynamic BLASes) and bind the result to your
	/// <see cref="RayTracingShader"/> via <see cref="RayTracingShader.BindAccelerationStructure"/>.
	/// </remarks>
	/// <param name="instances">
	/// The BLAS instances to include. Each instance carries its own world transform so a single
	/// BLAS can appear multiple times in the scene at different positions.
	/// </param>
	/// <returns>A new TLAS, or <c>null</c> if creation failed or ray tracing is unsupported.</returns>
	public static RayTracingAccelerationStructure BuildTLAS( IEnumerable<TLASInstance> instances )
	{
		if ( !Graphics.SupportsRayTracing )
			return null;

		if ( instances == null )
			throw new ArgumentNullException( nameof( instances ) );

		var instanceList = instances as IReadOnlyList<TLASInstance> ?? instances.ToList();
		if ( instanceList.Count == 0 )
			return null;

		// The native side allocates the instance buffer, fills in D3D12_RAYTRACING_INSTANCE_DESC /
		// VkAccelerationStructureInstanceKHR entries from the managed TLASInstance array, and issues
		// the top-level build command on the current command list.
		var nativeHandles = new object[instanceList.Count];
		var transforms = new Matrix[instanceList.Count];
		for ( int i = 0; i < instanceList.Count; i++ )
		{
			nativeHandles[i] = instanceList[i].Blas.native;
			transforms[i] = instanceList[i].Transform.ToMatrix();
		}

		var nativeTLAS = RenderTools.BuildTLAS( nativeHandles, transforms );
		if ( nativeTLAS == null )
			return null;

		return new RayTracingAccelerationStructure( nativeTLAS );
	}

	// ──────────────────────────────────────────────────────────────────────────────
	// Refit (dynamic geometry update)
	// ──────────────────────────────────────────────────────────────────────────────

	/// <summary>
	/// Refits this BLAS with updated vertex positions without rebuilding the full hierarchy.
	/// Only valid for dynamic acceleration structures (<see cref="IsDynamic"/> == <c>true</c>).
	/// </summary>
	/// <remarks>
	/// A refit is cheaper than a full rebuild but may degrade BVH quality over time for objects
	/// that deform significantly. Rebuilding from scratch is recommended every few frames for
	/// highly animated characters.
	/// </remarks>
	/// <param name="model">The scene model whose current skinned-mesh pose to refit against.</param>
	public void Update( SceneModel model )
	{
		if ( !IsValid() )
			throw new InvalidOperationException( "Cannot update an invalid acceleration structure." );

		if ( !IsDynamic )
			throw new InvalidOperationException( "Cannot refit a static (non-dynamic) acceleration structure. Rebuild it instead." );

		if ( model == null || !model.IsValid )
			throw new ArgumentNullException( nameof( model ) );

		// The native side re-uploads the current GPU skinned-mesh vertex buffer into the existing
		// BLAS scratch and issues a refit-only build (D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PERFORM_UPDATE).
		RenderTools.RefitBLAS( native, model.native );
	}

	// ──────────────────────────────────────────────────────────────────────────────
	// Lifecycle
	// ──────────────────────────────────────────────────────────────────────────────

	/// <summary>Releases the native acceleration structure resources.</summary>
	public void Dispose()
	{
		if ( native != null )
		{
			RenderTools.DestroyAccelerationStructure( native );
			native = null;
		}

		IsBuilt = false;
	}
}

/// <summary>
/// A single instance entry for building a Top-Level Acceleration Structure (TLAS).
/// </summary>
/// <param name="Blas">The Bottom-Level Acceleration Structure representing this instance's geometry.</param>
/// <param name="Transform">The world-space transform for this instance.</param>
public record struct TLASInstance( RayTracingAccelerationStructure Blas, Transform Transform );
