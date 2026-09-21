using System.Collections.Generic;

namespace Anvil.Models
{
	/// <summary>
	/// Where the range ruler's spoke starts: the loaded radar SITE (the default — a radius, ring to ring) or
	/// the user's LOCATION marker (a ray from you to wherever it meets the ring).
	/// </summary>
	/// <remarks>
	/// ⚠️ TOKENS, persisted as strings, for the same reason as <see cref="DistanceUnits"/>: an unrecognized
	/// value falls back to <see cref="Site"/> rather than failing a settings load.
	/// ⚠️ <see cref="Location"/> with no location marker placed is NOT an error and is not rewritten to Site —
	/// the page falls back for as long as there is no marker and says so on the ruler's chip, and the
	/// preference is still there the moment a marker is dropped.
	/// </remarks>
	public static class RulerAnchors
	{
		/// <summary>From the radar site's true location — the default.</summary>
		public const string Site = "Site";

		/// <summary>From the user-location marker (Markers → <c>UserLocationMarker</c>).</summary>
		public const string Location = "Location";

		/// <summary>Every token, in picker order. ⚠️ Parallel to <see cref="Labels"/>.</summary>
		public static IReadOnlyList<string> All { get; } = new[] { Site, Location };

		/// <summary>The picker's labels, in <see cref="All"/> order.</summary>
		public static IReadOnlyList<string> Labels { get; } = new[] { "Radar site", "My location" };

		/// <summary>Maps anything to a token this build knows; <see cref="Site"/> for the unrecognized.</summary>
		public static string Normalize(string? token) => token == Location ? Location : Site;

		/// <summary>The token's picker index.</summary>
		public static int IndexOf(string? token) => Normalize(token) == Location ? 1 : 0;

		/// <summary>The token at a picker index, clamped — out of range reads as <see cref="Site"/>.</summary>
		public static string FromIndex(int index) => index == 1 ? Location : Site;
	}
}
