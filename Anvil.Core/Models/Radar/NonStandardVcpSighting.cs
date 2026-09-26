using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Anvil.Models
{
	/// <summary>
	/// One site's record of one scan pattern that isn't in <c>VcpCatalog</c> — the evidence behind the
	/// Atlas's "Non-standard VCPs" list, and what you'd need to add the pattern to the catalog.
	/// </summary>
	public sealed class NonStandardVcpSighting
	{
		public string SiteId { get; set; } = string.Empty;
		public int Vcp { get; set; }

		/// <summary>Earliest / latest FRAME time seen on this pattern (radar time, not when Anvil looked).</summary>
		public DateTimeOffset FirstSeenUtc { get; set; }
		public DateTimeOffset LastSeenUtc { get; set; }

		/// <summary>Distinct tilt angles from the most recent sighting's elevation table.</summary>
		public List<float> Tilts { get; set; } = new();

		/// <summary>Distinct frame times (to the minute), newest last, capped — so a frame re-read from the
		/// cache or re-polled live never counts twice.</summary>
		public List<DateTimeOffset> FrameTimes { get; set; } = new();

		[JsonIgnore]
		public int Frames => FrameTimes.Count;
	}
}
