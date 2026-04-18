using NativeEngine;

namespace Sandbox;

public static partial class Graphics
{
	private static bool? _supportsRayTracingCached;

	/// <summary>
	/// Returns <c>true</c> when the current GPU and driver support real-time hardware ray tracing
	/// (DXR Tier 1.0+ on D3D12, or VK_KHR_ray_tracing_pipeline on Vulkan).
	/// </summary>
	/// <remarks>
	/// The result is cached after the first query. Always check this property before creating
	/// <see cref="RayTracingAccelerationStructure"/> instances or dispatching a
	/// <see cref="RayTracingShader"/>.
	/// </remarks>
	public static bool SupportsRayTracing
	{
		get
		{
			_supportsRayTracingCached ??= QueryRayTracingSupport();
			return _supportsRayTracingCached.Value;
		}
	}

	/// <summary>
	/// Invalidates the cached ray-tracing capability so it is re-queried on next access.
	/// Call after a video-mode / device reset.
	/// </summary>
	internal static void InvalidateRayTracingCapabilityCache()
	{
		_supportsRayTracingCached = null;
	}

	private static bool QueryRayTracingSupport()
	{
		// On Vulkan, the engine exposes VkPhysicalDeviceRayTracingPipelinePropertiesKHR via
		// DSI_VULKAN_RAY_TRACING_PIPELINE_PROPERTIES. A non-null pointer means the device
		// supports the extension and it was enabled at device-creation time.
		var rtProps = g_pRenderDevice.GetDeviceSpecificInfo( DeviceSpecificInfo_t.DSI_VULKAN_RAY_TRACING_PIPELINE_PROPERTIES );
		if ( rtProps != IntPtr.Zero )
			return true;

		// On D3D12, check the DXR support tier exposed through the native device.
		// RenderTools.GetDXRTier() returns 0 when unsupported, ≥ 1 when DXR Tier 1.0+.
		if ( RenderTools.GetDXRTier() >= 1 )
			return true;

		return false;
	}
}
