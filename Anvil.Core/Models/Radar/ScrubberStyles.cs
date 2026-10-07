using System.Collections.Generic;

namespace Anvil.Models
{
	/// <summary>
	/// Which loop scrubber the bar's radar console draws (Settings → Radar Console): the CLASSIC solid cells, or the
	/// DOT MATRIX (Primitives/DotMatrixScrubber — each frame a block of dots that fill as the frame loads).
	/// </summary>
	/// <remarks>⚠️ TOKENS, persisted as strings (<see cref="Services.AppSettings.ScrubberStyle"/>), like
	/// <see cref="RulerAnchors"/>: an unrecognized value falls back to <see cref="Dots"/>.</remarks>
	public static class ScrubberStyles
	{
		/// <summary>One solid cell per frame, a playhead bar over it.</summary>
		public const string Classic = "Classic";

		/// <summary>Each frame a block of dots that fill as it loads; the current frame lights whole — the default.</summary>
		public const string Dots = "Dots";

		/// <summary>Every token, in picker order. ⚠️ Parallel to <see cref="Labels"/>.</summary>
		public static IReadOnlyList<string> All { get; } = new[] { Classic, Dots };

		/// <summary>The picker's labels, in <see cref="All"/> order.</summary>
		public static IReadOnlyList<string> Labels { get; } = new[] { "Classic cells", "Dot matrix" };

		/// <summary>Maps anything to a token this build knows; <see cref="Dots"/> for the unrecognized.</summary>
		public static string Normalize(string? token) => token == Classic ? Classic : Dots;

		/// <summary>The token's picker index.</summary>
		public static int IndexOf(string? token) => Normalize(token) == Classic ? 0 : 1;

		/// <summary>The token at a picker index, clamped — out of range reads as <see cref="Dots"/>.</summary>
		public static string FromIndex(int index) => index == 0 ? Classic : Dots;
	}
}
