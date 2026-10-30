using System.Collections.Generic;

namespace Anvil.Models
{
	/// <summary>
	/// Which loop scrubber the bar's radar console draws (Settings → Radar Console): the CLASSIC solid cells, the
	/// DOT MATRIX (Primitives/DotMatrixScrubber — each frame a block of dots that fill as the frame loads), or the HATCH
	/// (Primitives/HatchScrubber — slanted cells that cross-hatch as the frame loads; the edge hatch's language).
	/// </summary>
	/// <remarks>⚠️ TOKENS, persisted as strings (<see cref="Services.AppSettings.ScrubberStyle"/>), like
	/// <see cref="RulerAnchors"/>: an unrecognized value falls back to <see cref="Dots"/>.</remarks>
	public static class ScrubberStyles
	{
		/// <summary>One solid cell per frame, a playhead bar over it.</summary>
		public const string Classic = "Classic";

		/// <summary>Each frame a block of dots that fill as it loads; the current frame lights whole — the default.</summary>
		public const string Dots = "Dots";

		/// <summary>Each frame a slanted cell whose hatching thickens as it loads (<see cref="HatchScrubberLadder"/>); the
		/// current frame solid.</summary>
		public const string Hatch = "Hatch";

		/// <summary>Every token, in picker order. ⚠️ Parallel to <see cref="Labels"/>.</summary>
		public static IReadOnlyList<string> All { get; } = new[] { Classic, Dots, Hatch };

		/// <summary>The picker's labels, in <see cref="All"/> order.</summary>
		public static IReadOnlyList<string> Labels { get; } = new[] { "Classic cells", "Dot matrix", "Hatch" };

		/// <summary>Maps anything to a token this build knows; <see cref="Dots"/> for the unrecognized.</summary>
		public static string Normalize(string? token) => token switch
		{
			Classic => Classic,
			Hatch => Hatch,
			_ => Dots,
		};

		/// <summary>The token's picker index.</summary>
		public static int IndexOf(string? token) => Normalize(token) switch
		{
			Classic => 0,
			Hatch => 2,
			_ => 1,
		};

		/// <summary>The token at a picker index — anything else reads as <see cref="Dots"/>.</summary>
		public static string FromIndex(int index) => index switch
		{
			0 => Classic,
			2 => Hatch,
			_ => Dots,
		};
	}
}
