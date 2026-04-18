using Sandbox;
using Sandbox.Engine.Settings;

/// <summary>
/// Demonstrates hardware RTX ray-traced shadows and reflections.
///
/// Add this component to any <see cref="GameObject"/> in a scene that has at least one
/// light and a reflective surface. On hardware that doesn't support ray tracing the
/// component logs a warning and disables itself gracefully.
/// </summary>
public sealed class RayTracingDemo : Component
{
	/// <summary>
	/// The quality level to apply when the demo starts.
	/// </summary>
	[Property]
	public RayTracingQuality Quality { get; set; } = RayTracingQuality.High;

	/// <summary>
	/// When true the demo rotates all child scene models to show dynamic RT updates.
	/// </summary>
	[Property]
	public bool AnimateObjects { get; set; } = true;

	/// <summary>Rotation speed in degrees per second.</summary>
	[Property]
	public float RotationSpeed { get; set; } = 45.0f;

	// BLAS instances for all SceneModels that are children of this object.
	private readonly List<(SceneModel model, RayTracingAccelerationStructure blas)> _blasInstances = new();

	// The current-frame TLAS.  Rebuilt each frame from the BLAS list.
	private RayTracingAccelerationStructure _tlas;

	protected override void OnStart()
	{
		if ( !Graphics.SupportsRayTracing )
		{
			Log.Warning( "RayTracingDemo: Hardware ray tracing is not supported on this device. The demo will run in fallback (screen-space) mode." );
			Enabled = false;
			return;
		}

		// Apply the chosen quality setting.
		RenderSettings.Instance.RayTracingQuality = Quality;

		Log.Info( $"RayTracingDemo: Ray tracing enabled at quality = {Quality}" );

		BuildBLASInstances();
	}

	protected override void OnUpdate()
	{
		if ( AnimateObjects )
		{
			foreach ( var child in GameObject.Children )
			{
				child.LocalRotation = child.LocalRotation.RotateAroundAxis( Vector3.Up, RotationSpeed * Time.Delta );
			}
		}

		RebuildTLAS();
	}

	protected override void OnDestroy()
	{
		DisposeBLASInstances();
		DisposeTLAS();
	}

	// ─── Private helpers ─────────────────────────────────────────────────────

	private void BuildBLASInstances()
	{
		DisposeBLASInstances();

		foreach ( var child in GameObject.Children )
		{
			var sceneModel = child.Components.Get<ModelRenderer>()?.SceneObject as SceneModel;
			if ( sceneModel == null )
				continue;

			// Determine whether the model is skinned / dynamic.
			bool isDynamic = sceneModel.Model?.BoneCount > 0;

			var blas = RayTracingAccelerationStructure.CreateFromModel( sceneModel, isDynamic );
			if ( blas != null )
			{
				_blasInstances.Add( (sceneModel, blas) );
				Log.Trace( $"RayTracingDemo: Built BLAS for '{child.Name}' (dynamic={isDynamic})" );
			}
		}
	}

	private void RebuildTLAS()
	{
		if ( _blasInstances.Count == 0 )
			return;

		// Refit any dynamic BLASes (skinned meshes, animated geometry).
		foreach ( var (model, blas) in _blasInstances )
		{
			if ( blas.IsDynamic && blas.IsValid() )
				blas.Update( model );
		}

		// Build TLAS instances with current world transforms.
		var instances = new List<TLASInstance>( _blasInstances.Count );
		foreach ( var (model, blas) in _blasInstances )
		{
			if ( blas.IsValid() )
				instances.Add( new TLASInstance( blas, model.Transform ) );
		}

		// Dispose the previous TLAS before building a new one.
		DisposeTLAS();
		_tlas = RayTracingAccelerationStructure.BuildTLAS( instances );
	}

	private void DisposeBLASInstances()
	{
		foreach ( var (_, blas) in _blasInstances )
			blas.Dispose();
		_blasInstances.Clear();
	}

	private void DisposeTLAS()
	{
		_tlas?.Dispose();
		_tlas = null;
	}
}
