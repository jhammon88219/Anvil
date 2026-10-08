namespace Anvil.Services
{
	/// <summary>
	/// DEV-ONLY PastCast frame-cap override (Settings → Dev → Frame cap experiment): replaces the 40-frame memory cap
	/// (<c>RadarViewModel.PastEventMaxFrames</c>) and lets EVERY window keep every 0.5° pass (no overview fallback), so a
	/// long SAILS window loads far more frames than the app allows. 0 = off (the default; a Release build never sets it).
	/// </summary>
	/// <remarks>
	/// ⚠️ It exists to MEASURE the renderer's real memory ceiling (docs/radar-rescan-frames.md, "the cap question"): the
	/// 40-frame cap rests on the assumption that decoded geometry counts against the ~4,192 MB JS heap
	/// (performance.memory.jsHeapSizeLimit). Load bigger loops and read the <c>cat:"memory"</c> samples: if heapMb climbs
	/// with retainedMb, the cap is real; if it stays flat, typed-array geometry lives outside the heap and the cap can rise.
	/// It can crash the map (black map, chrome fine) — that is a result, not a bug. Process-wide, not persisted.
	/// </remarks>
	public static class DevFrameCap
	{
		private static volatile int _frames;

		/// <summary>The override frame cap; 0 = off (the app's own cap and window rules apply).</summary>
		public static int Frames
		{
			get => _frames;
			set => _frames = value < 0 ? 0 : value;
		}

		public static bool IsOn => _frames > 0;
	}
}
