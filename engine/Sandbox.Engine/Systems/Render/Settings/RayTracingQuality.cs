namespace Sandbox.Engine.Settings;

/// <summary>
/// Controls whether hardware ray tracing is enabled and at which quality level.
/// The engine falls back gracefully to screen-space equivalents when set to <see cref="Off"/>
/// or when the hardware does not support ray tracing.
/// </summary>
public enum RayTracingQuality
{
	/// <summary>Ray tracing is disabled. Screen-space shadows and reflections are used instead.</summary>
	Off = 0,

	/// <summary>
	/// Ray-traced shadows and reflections at quarter resolution with temporal upscaling.
	/// Suitable for mid-range GPUs (GTX 16-series / RX 5000-series and above).
	/// </summary>
	Low = 1,

	/// <summary>
	/// Ray-traced shadows and reflections at half resolution with temporal upscaling.
	/// Suitable for RTX 20-series / RX 6000-series and above.
	/// </summary>
	Medium = 2,

	/// <summary>
	/// Full-resolution ray-traced shadows and reflections.
	/// Recommended for RTX 30-series / RX 7000-series and above.
	/// </summary>
	High = 3
}
