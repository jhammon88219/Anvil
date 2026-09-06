using System.Collections.Generic;
using System.Linq;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Default <see cref="IThemeProvider"/>. Hardcodes the themes for now, the same way
	/// <see cref="StyleProvider"/> hardcodes the basemap styles it pairs with.
	/// </summary>
	/// <remarks>
	/// ⚠️ TWO THEMES, and that is a DECISION, not a stopping point (2026-09-05). The alternative on the
	/// table was one identity per bundled basemap — five — which the app's shape already allows, since
	/// every theme declares its own <see cref="ThemeBase"/> and two may share one. Two was chosen because
	/// it is the smallest thing that proves the switch end to end, and because at exactly two the WinUI
	/// palette can stay in Controls/Styles.xaml's ThemeDictionaries, which support precisely
	/// Default/Light/HighContrast and nothing more.
	/// ⚠️ SO THE THIRD THEME IS NOT FREE. It cannot be a row in this array: three identities (or two
	/// sharing a base) need the ~15 override brushes moved out of XAML into C#, with the SolidColorBrush
	/// instances kept alive and their .Color mutated in place — swapping merged dictionaries at runtime
	/// does not reliably re-resolve elements that are already loaded. The upgrade is contained (the brush
	/// KEYS never change, only where their values come from), but it is a step, not an entry.
	/// ⚠️ CONSEQUENCE, knowingly accepted: three bundled styles (style.json, style-dark, dataVizGrayscale)
	/// are not any theme's, so the Basemap picker stays as a second control that can override the theme's
	/// choice for the session. That duplication is deliberate and temporary — see docs/theming.md.
	/// ⚠️ Both DisplayNames are PLACEHOLDERS — nothing has named these identities yet.
	/// </remarks>
	public sealed class ThemeProvider : IThemeProvider
	{
		// ⚠️⚠️ A THEME AND ITS BASEMAP ARE A NAMED PAIR, ONE TO ONE. Light takes Data Viz Light, Dark takes
		// Data Viz Black, and a future AnvilGray takes Data Viz Grayscale — an identity does not borrow
		// another identity's map. So when a theme is too bright or too dark, the fix is that theme's OWN
		// style file, never a re-point at someone else's.
		// ⚠️ This was learned the wrong way round (2026-09-06): anvilLight was briefly re-pointed at
		// dataVizGrayscale to cure the glare, which both broke the pairing and spent the map the grey
		// identity is for. Data Viz Light was dimmed instead — see docs/theming.md.
		// ⚠️ CONSEQUENCE: the five bundled styles imply up to FIVE identities, so the third one is coming,
		// and the third is what forces the ~15 override brushes out of XAML and into C# (see the remarks
		// above). Adding AnvilGray is that step, not a row in this array.
		private static readonly AppTheme[] Themes =
		{
			// ⚠️ Each GroundColor must match --anvil-ground in that theme's block in
			// Assets/Map/theme.css — the value is written in both places. See AppTheme.
			// ⚠️ MapStyleId is matched against IStyleProvider EXACTLY, and a miss falls back to the first
			// style in that list without a word. Note "dataVizlight" — lowercase L, which is genuinely how
			// StyleProvider spells it. Tidying that casing there without changing it here would silently
			// land this theme on the Regular basemap.
			new AppTheme("anvilDark", "Anvil Dark", ThemeBase.Dark, "dataVizBlack", "#0A0A0A"),
			new AppTheme("anvilLight", "Anvil Light", ThemeBase.Light, "dataVizlight", "#D8D8D8")
		};

		public IReadOnlyList<AppTheme> GetThemes() => Themes;

		public AppTheme Default => Themes[0];

		public AppTheme Resolve(string? id) =>
			string.IsNullOrWhiteSpace(id)
				? Default
				: Themes.FirstOrDefault(t => t.Id == id) ?? Default;
	}
}
