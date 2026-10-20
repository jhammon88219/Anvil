using System;

namespace Anvil.Models
{
	/// <summary>
	/// The ONE rule for whether an online tile URL can draw a map — read by <c>MapViewModel.CanUseOnlineTiles</c>
	/// (the live tile source) and <c>BasemapSetupViewModel</c> (whether to show map setup at launch).
	/// </summary>
	public static class OnlineTileUrls
	{
		/// <summary>
		/// Whether <paramref name="url"/> is usable. The Protomaps default ships WITHOUT a key (we can't supply one),
		/// and a keyless URL renders nothing — so a bare <c>key=</c> reads as not-yet-configured rather than being
		/// pushed to the map and blanking it. A URL with no <c>key=</c> at all is a self-hosted source and needs none.
		/// </summary>
		public static bool IsUsable(string? url)
		{
			if (string.IsNullOrWhiteSpace(url))
			{
				return false;
			}

			var i = url.IndexOf("key=", StringComparison.OrdinalIgnoreCase);
			if (i < 0)
			{
				return true; // a self-hosted source needs no key
			}

			var after = url[(i + 4)..];
			var end = after.IndexOf('&');
			return (end < 0 ? after : after[..end]).Length > 0;
		}
	}
}
