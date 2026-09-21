using System.Collections.Generic;
using System.Globalization;

namespace Anvil.Models
{
	/// <summary>
	/// The colour choices for the radar SCOPE furniture the user draws with — the range ring and the range
	/// ruler, which share one colour so the ruler reads as belonging to the ring it ends on. An empty token
	/// means "the theme's own colour" (theme.css <c>--anvil-scope-ring</c> / <c>--anvil-ruler-ink</c>).
	/// </summary>
	/// <remarks>
	/// ⚠️ PRESETS, NOT A FREE PICKER, on purpose. The ring draws at ~0.55 opacity over returns and the ruler
	/// over a dark casing: a free colour wheel invites a near-black ring on the dark basemap that simply
	/// vanishes. Each preset here is bright enough to survive both. What it can't guarantee is the LIGHT
	/// theme — White and Yellow wash out over pale earth; "Theme default" is the one that adapts.
	/// ⚠️ The sweep pulse is NOT covered: it is a warm afterglow deliberately unlike the ring (theme.css
	/// explains), and recolouring it with the ring would merge the two into one object.
	/// ⚠️ The token is the HEX itself (or empty), persisted verbatim and pushed into the page as a CSS
	/// custom-property override, so <see cref="Normalize"/> must only ever let through <c>#RRGGBB</c>.
	/// </remarks>
	public static class ScopeColors
	{
		/// <summary>The theme's own colour — the default, and what anything unrecognized falls back to.</summary>
		public const string ThemeDefault = "";

		/// <summary>The presets in picker order: token (hex, or empty) and a name for the tooltip.</summary>
		public static IReadOnlyList<(string Hex, string Name)> Presets { get; } = new[]
		{
			(ThemeDefault, "Theme default"),
			("#22D3EE", "Cyan"),
			("#FFFFFF", "White"),
			("#FFD60A", "Yellow"),
			("#FF4FD8", "Magenta"),
			("#7CFF4F", "Lime"),
			("#FF9F1A", "Orange"),
		};

		/// <summary>
		/// An upper-case <c>#RRGGBB</c> for a well-formed hex (a preset or a hand-edited file), else the theme
		/// default. The gate for anything read from disk: the result is written into the page's CSS.
		/// </summary>
		public static string Normalize(string? token)
		{
			if (token is not { Length: 7 } || token[0] != '#')
			{
				return ThemeDefault;
			}
			for (var i = 1; i < 7; i++)
			{
				if (!System.Uri.IsHexDigit(token[i]))
				{
					return ThemeDefault;
				}
			}
			return token.ToUpper(CultureInfo.InvariantCulture);
		}

		/// <summary>The preset index of a token; -1 for a valid hex that isn't a preset (a hand-edited file).</summary>
		public static int IndexOf(string? token)
		{
			var hex = Normalize(token);
			for (var i = 0; i < Presets.Count; i++)
			{
				if (Presets[i].Hex == hex)
				{
					return i;
				}
			}
			return -1;
		}

		/// <summary>The token at a picker index, clamped — out of range reads as the theme default.</summary>
		public static string FromIndex(int index) =>
			index >= 0 && index < Presets.Count ? Presets[index].Hex : ThemeDefault;
	}
}
